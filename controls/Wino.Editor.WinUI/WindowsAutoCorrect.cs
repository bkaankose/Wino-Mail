using System.Runtime.InteropServices;

namespace Wino.Editor;

// Keep the Windows spelling COM ABI inside the editor. A replacement is applied only when
// the provider explicitly marks it as safe to replace without asking the user.
internal static unsafe partial class WindowsAutoCorrect
{
    private static readonly Guid FactoryClass = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private static readonly Guid FactoryInterface = new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    public static string? GetReplacement(string language, string word)
    {
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(word)) return null;

        // Task.Run callers use an MTA thread. Initialize COM for this request.
        int initialization = CoInitializeEx(null, 0);
        if (initialization < 0) return null;
        void* factory = null;
        void* checker = null;
        void* errors = null;
        void* error = null;
        void* suggestions = null;
        try
        {
            if (CoCreateInstance(in FactoryClass, null, 1, in FactoryInterface, &factory) < 0 || factory == null)
                return null;

            var factoryTable = *(void***)factory;
            int supported = 0;
            fixed (char* languagePtr = language)
            {
                if (((delegate* unmanaged[Stdcall]<void*, char*, int*, int>)factoryTable[4])(factory, languagePtr, &supported) < 0 || supported == 0)
                    return null;
                if (((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)factoryTable[5])(factory, languagePtr, &checker) < 0 || checker == null)
                    return null;
            }

            var checkerTable = *(void***)checker;
            fixed (char* wordPtr = word)
            {
                if (((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)checkerTable[4])(checker, wordPtr, &errors) < 0 || errors == null)
                    return null;
            }

            var errorsTable = *(void***)errors;
            if (((delegate* unmanaged[Stdcall]<void*, void**, int>)errorsTable[3])(errors, &error) != 0 || error == null)
                return null;

            var errorTable = *(void***)error;
            uint start = 0, length = 0;
            int action = 0;
            if (((delegate* unmanaged[Stdcall]<void*, uint*, int>)errorTable[3])(error, &start) < 0 ||
                ((delegate* unmanaged[Stdcall]<void*, uint*, int>)errorTable[4])(error, &length) < 0 ||
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)errorTable[5])(error, &action) < 0 ||
                start != 0 || length != word.Length)
                return null;

            if (action == 1)
            {
                fixed (char* wordPtr = word)
                {
                    if (((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)checkerTable[5])(checker, wordPtr, &suggestions) < 0 || suggestions == null)
                        return null;
                }
                var suggestionsTable = *(void***)suggestions;
                string? first = null;
                int bestRank = 0;
                bool ambiguous = false;
                for (int index = 0; index < 5; index++)
                {
                    char* item = null;
                    uint fetched = 0;
                    int status = ((delegate* unmanaged[Stdcall]<void*, uint, char**, uint*, int>)suggestionsTable[3])(suggestions, 1, &item, &fetched);
                    if (status != 0 || fetched == 0 || item == null) break;
                    string candidate;
                    try { candidate = new string(item); }
                    finally { Marshal.FreeCoTaskMem((nint)item); }
                    int rank = SingleEditRank(word, candidate);
                    if (rank == 0) continue;
                    if (rank > bestRank) { first = candidate; bestRank = rank; ambiguous = false; }
                    else if (rank == bestRank) ambiguous = true;
                }
                return ambiguous ? null : first;
            }
            if (action != 2) return null;

            char* replacement = null;
            if (((delegate* unmanaged[Stdcall]<void*, char**, int>)errorTable[6])(error, &replacement) < 0 || replacement == null)
                return null;
            try
            {
                var result = new string(replacement);
                return result.Length > 0 && result.Length <= 64 && !string.Equals(result, word, StringComparison.Ordinal)
                    ? result : null;
            }
            finally { Marshal.FreeCoTaskMem((nint)replacement); }
        }
        catch (COMException) { return null; }
        finally
        {
            Release(error);
            Release(suggestions);
            Release(errors);
            Release(checker);
            Release(factory);
            CoUninitialize();
        }
    }

    private static int SingleEditRank(string source, string target)
    {
        if (source.Length < 4 || target.Length < 4 || target.Length > 64 || Math.Abs(source.Length - target.Length) > 1 ||
            !string.Equals(source, source.ToLowerInvariant(), StringComparison.Ordinal)) return 0;
        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();
        int prefix = 0;
        while (prefix < source.Length && prefix < target.Length && source[prefix] == target[prefix]) prefix++;
        if (source.Length == target.Length)
        {
            if (prefix == source.Length) return 0;
            if (source.AsSpan(prefix + 1).SequenceEqual(target.AsSpan(prefix + 1))) return 1;
            return prefix + 1 < source.Length && source[prefix] == target[prefix + 1] &&
                source[prefix + 1] == target[prefix] &&
                source.AsSpan(prefix + 2).SequenceEqual(target.AsSpan(prefix + 2)) ? 4 : 0;
        }
        return source.Length < target.Length
            ? (source.AsSpan(prefix).SequenceEqual(target.AsSpan(prefix + 1)) ? 3 : 0)
            : (source.AsSpan(prefix + 1).SequenceEqual(target.AsSpan(prefix)) ? 2 : 0);
    }

    private static void Release(void* instance)
    {
        if (instance == null) return;
        var table = *(void***)instance;
        ((delegate* unmanaged[Stdcall]<void*, uint>)table[2])(instance);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, void* outer, uint context, in Guid interfaceId, void** result);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(void* reserved, uint apartment);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
