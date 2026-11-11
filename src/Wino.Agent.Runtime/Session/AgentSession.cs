using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Agent.Runtime.Reading;
using Wino.Agent.Runtime.Resolve;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Session;

/// <summary>One assistant window: handle a request, answer its questions, confirm the plan, report the outcome.</summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Text (typed or transcribed) and selection in; a question, a plan, a message or nothing out.</summary>
    Task<AgentTurn> HandleAsync(AgentRequest request, CancellationToken ct);

    /// <summary>Continues the pending turn with the answer to its clarification.</summary>
    Task<AgentTurn> AnswerAsync(ClarificationAnswer answer, CancellationToken ct);

    /// <summary>The user accepted (some of) the plan's steps: returns the plan to run, or nothing.</summary>
    Task<AgentTurn> ConfirmAsync(PlanConfirmation confirmation, CancellationToken ct);

    /// <summary>The executor's result, for the conversation context and the "Remember this?" proposals.</summary>
    Task ReportAsync(PlanOutcome outcome, CancellationToken ct);

    /// <summary>"New conversation": forgets the context and any pending turn.</summary>
    void Reset();

    /// <summary>The turn in progress, if a clarification is pending.</summary>
    Clarification? PendingClarification { get; }

    /// <summary>
    /// The read request this session would send for <paramref name="request"/> (clock, locale, selection description,
    /// previous turns). A spoken request opens the socket with it and streams audio; the reading frame then comes back
    /// to <see cref="HandleAsync"/> as <see cref="AgentRequest.PreRead"/>.
    /// </summary>
    AssistantReadRequest CreateReadRequest(AgentRequest request);
}

/// <summary>The turn loop: read -> resolve -> clarify -> policy -> plan, over the conversation context.</summary>
public sealed class AgentSession : IAgentSession
{
    private readonly IReadingClient _reader;
    private readonly IAgentDomainQueries _queries;
    private readonly AgentBrain _brain;
    private readonly IAgentClock _clock;
    private readonly IAgentPrompter? _prompter;
    private readonly Proposals _proposals;
    private readonly ConversationContext _context = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TurnState? _turn;
    private ResolvedPlan? _lastPlan;
    private ConversationContext.Turn? _lastTurn;

    public AgentSession(IReadingClient reader, IAgentDomainQueries queries, AgentBrain brain, IAgentClock clock, IAgentPrompter? prompter, AgentSessionOptions? options = null)
    {
        _reader = reader;
        _queries = queries;
        _brain = brain;
        _clock = clock;
        _prompter = prompter;
        _proposals = new Proposals(brain);
        Options = options ?? new AgentSessionOptions();
    }

    public AgentSessionOptions Options { get; }

    public ConversationContext Context => _context;

    public Clarification? PendingClarification => _turn?.Pending?.Clarification;

    /// <summary>Whether a compose call is made for composed content (off: the points are the draft).</summary>
    public bool ComposeEnabled { get; set; } = true;

    public async Task<AgentTurn> HandleAsync(AgentRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _turn = null;
            var now = _clock.LocalNow;
            var reading = request.PreRead ?? await _reader.ReadAsync(BuildReadRequest(request, now), ct).ConfigureAwait(false);
            var validated = ReadingValidator.Validate(reading);
            reading = validated.Reading;
            var turn = new TurnState { Request = request, Reading = reading, StartedAt = now };
            _lastTurn = new ConversationContext.Turn { At = now, Text = request.Text, Reading = reading };

            if (reading.Refusal is { } refusal)
                return Finish(turn, new AgentTurn.Message(refusal.Reason switch
                {
                    AssistantRefusalReasons.StandingRule => AgentMessageKind.StandingRuleRefused,
                    AssistantRefusalReasons.OutOfScope or AssistantRefusalReasons.Injection => AgentMessageKind.OutOfScope,
                    _ => AgentMessageKind.NotUnderstood,
                }, refusal.Say));
            if (reading.Kind == AssistantReadingKinds.Cancel) return Finish(turn, AgentTurn.Nothing.Instance);
            if (reading.Kind == AssistantReadingKinds.Confirm) return Finish(turn, _lastPlan is { } lp ? new AgentTurn.Plan(lp) : AgentTurn.Nothing.Instance);
            if (reading.Kind == AssistantReadingKinds.Chat && reading.Steps.Count == 0) return Finish(turn, new AgentTurn.Message(AgentMessageKind.OutOfScope));
            // "put new events in my Home calendar": a stated default or preference read as a conditional action is the statement
            // alone. Both predicates take closed values (a use, a key=value), so nothing injected can ride on them.
            if (reading.Steps.Count > 0 && reading.Steps.All(s => s.Cond is not null)
                && reading.Memory?.Any(m => m.Stated && m.Predicate is AssistantMemoryPredicates.Default or AssistantMemoryPredicates.Preference) == true)
            {
                reading = reading with { Kind = AssistantReadingKinds.Teach, Steps = [], Memory = reading.Memory.Where(m => m.Predicate is AssistantMemoryPredicates.Default or AssistantMemoryPredicates.Preference).ToList() };
                turn = new TurnState { Request = request, Reading = reading, StartedAt = now };
                _lastTurn = new ConversationContext.Turn { At = now, Text = request.Text, Reading = reading };
            }

            // A stated statement whose words are the whole request ("mails from X are my insurance" read as a move): the
            // classifier itself says the request asserts a fact and nothing else, so the steps are a misread.
            if (reading.Steps.Count > 0 && reading.Kind == AssistantReadingKinds.Command && request.Selection is null
                && reading.Memory?.Any(m => m.Stated && m.Predicate is AssistantMemoryPredicates.Synonym or AssistantMemoryPredicates.Label or AssistantMemoryPredicates.Nickname or AssistantMemoryPredicates.Relationship or AssistantMemoryPredicates.Group
                    && TextMatch.Key(m.Text) == TextMatch.Key(request.Text)) == true
                && reading.Steps.All(s => s.Cond is null && !AssistantActionCatalog.IsFind(s.Action)))
            {
                reading = reading with { Kind = AssistantReadingKinds.Teach, Steps = [] };
                turn = new TurnState { Request = request, Reading = reading, StartedAt = now };
                _lastTurn = new ConversationContext.Turn { At = now, Text = request.Text, Reading = reading };
            }

            if (reading.Steps.Any(s => s.Cond is not null))
            {
                var once = reading with { Steps = reading.Steps.Select(s => s with { Cond = null }).ToList(), Memory = [] };
                var gloss = string.Join(" ", reading.Steps.Select(s => s.Gloss).Where(g => g is not null));
                return Finish(turn, new AgentTurn.Message(AgentMessageKind.StandingRuleRefused, gloss.Length == 0 ? null : gloss, [new AgentFollowUp(AgentFollowUpKind.RunOnce, gloss, once)]));
            }

            if (reading.Steps.Count == 0 && (reading.Memory?.Count ?? 0) == 0 && reading.Discourse?.Undo != true)
                return Finish(turn, new AgentTurn.Message(reading.Kind == AssistantReadingKinds.Question ? AgentMessageKind.Answer : AgentMessageKind.NotUnderstood));

            _turn = turn;
            return await RunAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentTurn> AnswerAsync(ClarificationAnswer answer, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_turn?.Pending is not { } pending || pending.Clarification.Id != answer.Id) return AgentTurn.Nothing.Instance;
            if (answer.Cancelled)
            {
                _turn = null;
                return AgentTurn.Nothing.Instance;
            }

            _turn.Answers[pending.Field] = answer;
            _turn.Pending = null;
            return await RunAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<AgentTurn> ConfirmAsync(PlanConfirmation confirmation, CancellationToken ct)
    {
        if (_lastPlan is null || _lastPlan.Id != confirmation.PlanId) return Task.FromResult<AgentTurn>(AgentTurn.Nothing.Instance);
        var accepted = _lastPlan.Steps.Where(s => confirmation.AcceptedSteps.Contains(s.N)).ToList();
        // A step whose source step was declined cannot run.
        accepted = accepted.Where(s => s.DependsOn is null || accepted.Any(a => a.N == s.DependsOn)).ToList();
        if (accepted.Count == 0) return Task.FromResult<AgentTurn>(AgentTurn.Nothing.Instance);
        var plan = _lastPlan with { Steps = accepted };
        _lastPlan = plan;
        if (_lastTurn is not null) _lastTurn.Plan = plan;
        return Task.FromResult<AgentTurn>(new AgentTurn.Plan(plan));
    }

    public async Task ReportAsync(PlanOutcome outcome, CancellationToken ct)
    {
        var turn = _context.Turns(_clock.LocalNow).LastOrDefault(t => t.Plan?.Id == outcome.PlanId) ?? _lastTurn;
        if (turn is not null) turn.Outcome = outcome;
        if (outcome.Steps.Any(s => s.Status == StepStatus.Done))
            await _proposals.ShowAsync(_prompter, ct).ConfigureAwait(false);
        else _proposals.Clear();
    }

    public void Reset()
    {
        _turn = null;
        _lastPlan = null;
        _lastTurn = null;
        _context.Clear();
        _proposals.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }

    /// <summary>The proposals waiting for the plan's outcome (for tests and the memory screen).</summary>
    public IReadOnlyList<BrainProposal> PendingProposals => _proposals.Pending;

    public Task AnswerProposalAsync(BrainProposal proposal, bool yes, CancellationToken ct) => _proposals.AnswerAsync(proposal, yes, ct);

    // ---- the loop ------------------------------------------------------------------------------------------------

    private async Task<AgentTurn> RunAsync(CancellationToken ct)
    {
        var turn = _turn!;
        var index = await DomainIndex.LoadAsync(_queries, ct).ConfigureAwait(false);
        var resolver = new TurnResolver(turn, index, _brain, _queries, _clock, _context);
        turn.Learned.Clear();
        turn.Proposals.Clear();
        turn.Forget.Clear();
        turn.MemoryMessages.Clear();
        turn.Composes.Clear();
        turn.People.Clear();
        try
        {
            var plan = await resolver.ResolveAsync(ct).ConfigureAwait(false);
            await WriteLearnedAsync(turn, ct).ConfigureAwait(false);
            plan = await ComposeAsync(turn, plan, ct).ConfigureAwait(false);
            if (plan.Steps.Count == 0)
            {
                var kind = turn.MemoryMessages.Contains(AgentMessageKind.Remembered) ? AgentMessageKind.Remembered
                    : turn.MemoryMessages.Contains(AgentMessageKind.Forgotten) ? AgentMessageKind.Forgotten
                    : turn.Proposals.Count > 0 ? AgentMessageKind.ProposalSaved
                    : AgentMessageKind.NotUnderstood;
                // Proposals of a statement turn are shown at once: there is no plan whose outcome to wait for.
                foreach (var p in turn.Proposals) _proposals.Queue(p);
                _turn = null;
                var message = Finish(turn, new AgentTurn.Message(kind, turn.Reading.Memory?.FirstOrDefault()?.Text));
                await _proposals.ShowAsync(_prompter, ct).ConfigureAwait(false);
                return message;
            }

            foreach (var p in turn.Proposals) _proposals.Queue(p);
            _lastPlan = plan;
            _turn = null;
            if (_lastTurn is not null)
            {
                _lastTurn.Plan = plan;
                foreach (var (role, people) in turn.People) _lastTurn.People[role] = people;
            }

            return Finish(turn, new AgentTurn.Plan(plan));
        }
        catch (NeedsClarificationException e)
        {
            await WriteLearnedAsync(turn, ct).ConfigureAwait(false);
            return new AgentTurn.Clarify(e.Clarification, resolver.Partial);
        }
        catch (TurnMessageException e)
        {
            await WriteLearnedAsync(turn, ct).ConfigureAwait(false);
            _turn = null;
            return Finish(turn, new AgentTurn.Message(e.Kind, e.Gloss));
        }
    }

    private AgentTurn Finish(TurnState turn, AgentTurn result)
    {
        if (_lastTurn is not null && result is not AgentTurn.Clarify)
        {
            _context.Add(_lastTurn);
        }

        return result;
    }

    /// <summary>Every answered pick and stated fact is written before the turn returns, so a cancelled plan still remembers the pick.</summary>
    private async Task WriteLearnedAsync(TurnState turn, CancellationToken ct)
    {
        foreach (var id in turn.Forget.Distinct()) await _brain.ForgetAsync(id, ct).ConfigureAwait(false);
        foreach (var l in turn.Learned)
        {
            if (l.ReplaceOthers) await _brain.ReplaceAsync(l.Kind, l.Value, l.Subject, ct).ConfigureAwait(false);
            await _brain.RememberAsync(l.Kind, l.Subject, l.Value, l.Source, l.SaidAs, ct).ConfigureAwait(false);
        }

        turn.Forget.Clear();
        turn.Learned.Clear();
    }

    /// <summary>Composed content: one compose call per composed body or message, with the hint built from stated preferences.</summary>
    private async Task<ResolvedPlan> ComposeAsync(TurnState turn, ResolvedPlan plan, CancellationToken ct)
    {
        if (turn.Composes.Count == 0) return plan;
        var steps = plan.Steps.ToList();
        foreach (var c in turn.Composes)
        {
            var i = steps.FindIndex(s => s.N == c.StepN);
            if (i < 0) continue;
            var hints = HintBuilder.Build(_brain, c.Recipients, c.AccountId, c.Language, c.Tone, c.Length);
            // The hint already puts a stated preference before the request's own language; the compose request carries the result as its language too, since the server lets the request's fields win.
            var language = hints?.Language ?? c.Language ?? turn.Reading.Lang;
            AssistantComposeResult? composed = null;
            if (ComposeEnabled)
            {
                try
                {
                    composed = await _reader.ComposeAsync(new AssistantComposeRequest(c.Points.Take(AssistantComposeRequest.MaxPoints).Select(p => p.Length > AssistantComposeRequest.MaxPointCharacters ? p[..AssistantComposeRequest.MaxPointCharacters] : p).ToList(),
                        c.Kind, language, hints?.Tone ?? c.Tone, c.Length, hints), ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    composed = null;
                }
            }

            var step = steps[i];
            steps[i] = step.Args switch
            {
                StepArgs.DraftArgs d when c.Slot == "body" => step with
                {
                    Args = d with
                    {
                        Body = composed is null ? new DraftBody(string.Join("\n", c.Points), BodyKind.Points, language) : new DraftBody(composed.Text, BodyKind.Composed, language),
                        Subject = d.Subject ?? composed?.Subject,
                        Language = language,
                        Tone = c.Tone ?? hints?.Tone ?? d.Tone,
                    },
                },
                StepArgs.EventChangeArgs e when c.Slot == "message" => step with { Args = e with { Message = composed?.Text ?? string.Join("\n", c.Points) } },
                _ => step,
            };
        }

        return plan with { Steps = steps };
    }

    public AssistantReadRequest CreateReadRequest(AgentRequest request) => BuildReadRequest(request, _clock.LocalNow);

    private AssistantReadRequest BuildReadRequest(AgentRequest request, DateTime now)
    {
        var selection = request.Selection is { } s ? new AssistantSelection(s.Kind, Math.Max(1, s.Items.Count), s.Flags.Count == 0 ? null : s.Flags.ToList()) : null;
        return new AssistantReadRequest(request.Text, request.Input == AgentInputKind.Spoken ? AssistantInputKind.Spoken : AssistantInputKind.Typed,
            new AssistantClock(now, _clock.TimeZoneId, _clock.WeekStart, _clock.WorkDayStart, _clock.WorkDayEnd),
            new AssistantLocale(_clock.UiCulture, _clock.ExpectedLanguages.Count == 0 ? null : _clock.ExpectedLanguages.ToList()),
            selection, _context.Previous(now));
    }
}
