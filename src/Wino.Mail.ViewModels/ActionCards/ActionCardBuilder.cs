#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Mail.ViewModels.ActionCards;

/// <summary>
/// Turns the schema.org data a sender put in a message into cards. The data is the sender's claim and is shown as such:
/// nothing is fetched, and only web addresses (http and https) become buttons.
/// </summary>
public static class ActionCardBuilder
{
    private const int MaximumCards = 4;
    private const int MaximumOtherFields = 8;
    private const string SchemaPrefix = "http://schema.org/";
    private const string SecureSchemaPrefix = "https://schema.org/";

    public static List<ActionCardModel> Build(IReadOnlyList<MailStructuredItem> items, string senderName)
    {
        var cards = new List<ActionCardModel>();

        foreach (var item in Flatten(items))
        {
            if (cards.Count == MaximumCards)
                break;

            if (CreateCard(item, senderName) is { } card)
                cards.Add(card);
        }

        return cards;
    }

    // A sender may wrap its items in a list or in an item without a type of its own.
    private static IEnumerable<MailStructuredItem> Flatten(IReadOnlyList<MailStructuredItem> items)
    {
        foreach (var item in items)
        {
            if (item.Type.Length > 0)
            {
                yield return item;
                continue;
            }

            foreach (var property in item.Properties)
            {
                foreach (var nested in Items(property.Value).Where(static nested => nested.Type.Length > 0))
                    yield return nested;
            }
        }
    }

    private static ActionCardModel? CreateCard(MailStructuredItem item, string senderName)
        => WithoutRepeatedPrimary(CreateCardCore(item, senderName));

    // A card's main button is not offered a second time among its other buttons.
    private static ActionCardModel? WithoutRepeatedPrimary(ActionCardModel? card)
    {
        if (card?.PrimaryAction is { } primary)
            card.MoreActions.RemoveAll(action => action.Kind == primary.Kind && action.Address == primary.Address && action.Start == primary.Start);

        return card;
    }

    private static ActionCardModel? CreateCardCore(MailStructuredItem item, string senderName) => TypeName(item) switch
    {
        "FlightReservation" => Flight(item),
        "TrainReservation" => Trip(item, ActionCardKind.Train),
        "BusReservation" => Trip(item, ActionCardKind.Bus),
        "LodgingReservation" => Lodging(item),
        "RentalCarReservation" => RentalCar(item),
        "FoodEstablishmentReservation" => Restaurant(item),
        "EventReservation" => EventReservation(item),
        "Order" => Order(item, senderName),
        "ParcelDelivery" => Parcel(item, senderName),
        "Invoice" => Invoice(item, senderName),
        var type when type.EndsWith("Event", StringComparison.Ordinal) => Event(item, null),
        _ => Other(item, senderName)
    };

    #region Card kinds

    private static ActionCardModel? Flight(MailStructuredItem reservation)
    {
        var flight = Item(reservation, "reservationFor");
        if (flight is null)
            return null;

        var airline = Item(flight, "airline");
        var number = Join(" ", Text(airline, "iataCode"), Text(flight, "flightNumber"));
        var from = Item(flight, "departureAirport");
        var to = Item(flight, "arrivalAirport");
        var fromCode = FirstText(Text(from, "iataCode"), Text(from, "name"));
        var toCode = FirstText(Text(to, "iataCode"), Text(to, "name"));
        var departure = Moment(flight, "departureTime");
        var arrival = Moment(flight, "arrivalTime");
        var seat = FirstText(Text(Item(Item(reservation, "reservedTicket"), "ticketedSeat"), "seatNumber"), Text(reservation, "airplaneSeat"));
        var title = Join(" · ", number.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_FlightTitleFormat, number) : Translator.ActionCard_Flight, Route(fromCode, toCode));

        return new ActionCardModel
        {
            Kind = ActionCardKind.Flight,
            Glyph = WinoIconGlyph.Airplane,
            Title = title,
            Summary = Join(" · ", FormatMoment(departure), seat.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_SeatFormat, seat) : string.Empty),
            DepartureTime = FormatTime(departure),
            DeparturePlace = Join("\n", Join(" · ", fromCode, Text(from, "name") == fromCode ? string.Empty : Text(from, "name")), Terminal(flight, "departureTerminal")),
            ArrivalTime = FormatTime(arrival),
            ArrivalPlace = Join(" · ", toCode, Text(to, "name") == toCode ? string.Empty : Text(to, "name")),
            Fields = Fields(
                (Translator.ActionCard_Date, FormatDate(departure), false),
                (Translator.ActionCard_Passenger, Text(Item(reservation, "underName"), "name"), false),
                (Translator.ActionCard_Seat, seat, false),
                (Translator.ActionCard_Gate, Text(flight, "departureGate"), false),
                (Translator.ActionCard_BookingCode, Text(reservation, "reservationNumber"), true)),
            PrimaryAction = FirstSenderAction(reservation, "CheckInAction", Translator.ActionCard_CheckIn, "checkinUrl")
                            ?? CalendarAction(title, departure, arrival, fromCode),
            MoreActions = Actions(
                SenderLink(reservation, Translator.ActionCard_ManageBooking, "modifyReservationUrl", "url"),
                CalendarAction(title, departure, arrival, fromCode))
        };
    }

    private static ActionCardModel? Trip(MailStructuredItem reservation, ActionCardKind kind)
    {
        var trip = Item(reservation, "reservationFor");
        if (trip is null)
            return null;

        var isTrain = kind == ActionCardKind.Train;
        var number = FirstText(Text(trip, isTrain ? "trainNumber" : "busNumber"), Text(trip, isTrain ? "trainName" : "busName"));
        var from = FirstText(Text(Item(trip, isTrain ? "departureStation" : "departureBusStop"), "name"), Text(trip, "departureStation"));
        var to = FirstText(Text(Item(trip, isTrain ? "arrivalStation" : "arrivalBusStop"), "name"), Text(trip, "arrivalStation"));
        var departure = Moment(trip, "departureTime");
        var arrival = Moment(trip, "arrivalTime");
        var seat = Item(Item(reservation, "reservedTicket"), "ticketedSeat");
        var name = isTrain ? Translator.ActionCard_Train : Translator.ActionCard_Bus;
        var title = Join(" · ", number.Length > 0 ? $"{name} {number}" : name, Route(from, to));

        return new ActionCardModel
        {
            Kind = kind,
            Glyph = WinoIconGlyph.Train,
            Title = title,
            Summary = FormatMoment(departure),
            DepartureTime = FormatTime(departure),
            DeparturePlace = Join("\n", from, Text(trip, "departurePlatform") is { Length: > 0 } platform ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_PlatformFormat, platform) : string.Empty),
            ArrivalTime = FormatTime(arrival),
            ArrivalPlace = to,
            Fields = Fields(
                (Translator.ActionCard_Date, FormatDate(departure), false),
                (Translator.ActionCard_Passenger, Text(Item(reservation, "underName"), "name"), false),
                (Translator.ActionCard_Coach, Text(seat, "seatSection"), false),
                (Translator.ActionCard_Seat, Text(seat, "seatNumber"), false),
                (Translator.ActionCard_Ticket, FirstText(Text(Item(reservation, "reservedTicket"), "ticketNumber"), Text(reservation, "reservationNumber")), true)),
            PrimaryAction = SenderLink(reservation, Translator.ActionCard_ShowTicket, "url") ?? CalendarAction(title, departure, arrival, from),
            MoreActions = Actions(CalendarAction(title, departure, arrival, from))
        };
    }

    private static ActionCardModel? Lodging(MailStructuredItem reservation)
    {
        var place = Item(reservation, "reservationFor");
        var name = FirstText(Text(place, "name"), Translator.ActionCard_Stay);
        var checkIn = FirstMoment(reservation, "checkinTime", "checkinDate");
        var checkOut = FirstMoment(reservation, "checkoutTime", "checkoutDate");
        var nights = checkIn is { } start && checkOut is { } end ? (end.Date - start.Date).Days : 0;
        var address = Address(place);

        return new ActionCardModel
        {
            Kind = ActionCardKind.Lodging,
            Glyph = WinoIconGlyph.Bed,
            Title = name,
            Summary = Join(" · ", DateSpan(checkIn, checkOut), nights > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_NightsFormat, nights) : string.Empty),
            Subheadline = address,
            Fields = Fields(
                (Translator.ActionCard_CheckIn, FormatMoment(checkIn), false),
                (Translator.ActionCard_CheckOut, FormatMoment(checkOut), false),
                (Translator.ActionCard_Guests, Guests(reservation), false),
                (Translator.ActionCard_Confirmation, Text(reservation, "reservationNumber"), true)),
            PrimaryAction = CalendarAction(name, checkIn, checkOut, address, allDayWithoutTime: true),
            MoreActions = Actions(SenderLink(reservation, Translator.ActionCard_ViewReservation, "modifyReservationUrl", "url"))
        };
    }

    private static ActionCardModel? RentalCar(MailStructuredItem reservation)
    {
        var car = Item(reservation, "reservationFor");
        var pickUp = Moment(reservation, "pickupTime");
        var dropOff = Moment(reservation, "dropoffTime");
        var pickUpPlace = Text(Item(reservation, "pickupLocation"), "name");
        var dropOffPlace = Text(Item(reservation, "dropoffLocation"), "name");
        var carName = Join(" ", Text(Item(car, "brand"), "name"), FirstText(Text(car, "model"), Text(car, "name")));
        var title = carName.Length > 0 ? Join(" · ", Translator.ActionCard_RentalCar, carName) : Translator.ActionCard_RentalCar;

        return new ActionCardModel
        {
            Kind = ActionCardKind.RentalCar,
            Glyph = WinoIconGlyph.VehicleCar,
            Title = title,
            Summary = pickUp is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_PickUpFormat, FormatMoment(pickUp)),
            Fields = Fields(
                (Translator.ActionCard_PickUp, Join("\n", FormatMoment(pickUp), pickUpPlace), false),
                (Translator.ActionCard_Return, Join("\n", FormatMoment(dropOff), dropOffPlace), false),
                (Translator.ActionCard_Driver, Text(Item(reservation, "underName"), "name"), false),
                (Translator.ActionCard_Confirmation, Text(reservation, "reservationNumber"), true)),
            PrimaryAction = SenderLink(reservation, Translator.ActionCard_ManageBooking, "modifyReservationUrl", "url") ?? CalendarAction(title, pickUp, dropOff, pickUpPlace),
            MoreActions = Actions(CalendarAction(title, pickUp, dropOff, pickUpPlace))
        };
    }

    private static ActionCardModel? Restaurant(MailStructuredItem reservation)
    {
        var place = Item(reservation, "reservationFor");
        var name = FirstText(Text(place, "name"), Translator.ActionCard_Restaurant);
        var start = Moment(reservation, "startTime");
        var partySize = Text(reservation, "partySize");
        var address = Address(place);

        return new ActionCardModel
        {
            Kind = ActionCardKind.Restaurant,
            Glyph = WinoIconGlyph.Food,
            Title = name,
            Summary = Join(" · ", FormatMoment(start), partySize.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_PeopleFormat, partySize) : string.Empty),
            Subheadline = address,
            Fields = Fields(
                (Translator.ActionCard_When, FormatMoment(start), false),
                (Translator.ActionCard_TableFor, partySize, false),
                (Translator.ActionCard_Name, Text(Item(reservation, "underName"), "name"), false),
                (Translator.ActionCard_Confirmation, Text(reservation, "reservationNumber"), true)),
            PrimaryAction = CalendarAction(name, start, start?.AddHours(2), address),
            MoreActions = Actions(SenderLink(reservation, Translator.ActionCard_ChangeReservation, "modifyReservationUrl", "url"))
        };
    }

    private static ActionCardModel? EventReservation(MailStructuredItem reservation)
        => Item(reservation, "reservationFor") is { } item ? Event(item, reservation) : null;

    private static ActionCardModel? Event(MailStructuredItem item, MailStructuredItem? reservation)
    {
        var name = Text(item, "name");
        if (name.Length == 0)
            return null;

        var start = Moment(item, "startDate");
        var end = Moment(item, "endDate");
        var location = Item(item, "location");
        var venue = FirstText(Text(location, "name"), Text(item, "location"));
        var place = Join(", ", venue, Address(location));
        var ticket = Item(reservation, "reservedTicket");
        var seat = Item(ticket, "ticketedSeat");
        var tickets = Text(reservation, "numSeats");

        return new ActionCardModel
        {
            Kind = ActionCardKind.Event,
            Glyph = WinoIconGlyph.Ticket,
            Title = name,
            Summary = Join(" · ", FormatMoment(start), venue),
            Headline = MomentSpan(start, end),
            Subheadline = place,
            Fields = Fields(
                (Translator.ActionCard_Tickets, tickets, false),
                (Translator.ActionCard_Seat, Join(" · ", Text(seat, "seatSection"), Text(seat, "seatRow"), Text(seat, "seatNumber")), false),
                (Translator.ActionCard_Ticket, Text(ticket, "ticketNumber"), true),
                (Translator.ActionCard_Order, Text(reservation, "reservationNumber"), true)),
            PrimaryAction = CalendarAction(name, start, end, place),
            MoreActions = Actions(
                SenderLink(ticket, Translator.ActionCard_ViewTickets, "ticketDownloadUrl", "ticketPrintUrl", "url")
                ?? SenderLink(reservation, Translator.ActionCard_ViewTickets, "url")
                ?? SenderLink(item, Translator.ActionCard_Open, "url"))
        };
    }

    private static ActionCardModel? Order(MailStructuredItem order, string senderName)
    {
        var merchant = FirstText(Text(Item(order, "merchant"), "name"), Text(Item(order, "seller"), "name"), senderName);
        var number = Text(order, "orderNumber");
        var lines = new List<ActionCardLine>();

        foreach (var offer in Items(Value(order, "acceptedOffer")).Take(8))
        {
            var product = FirstText(Text(Item(offer, "itemOffered"), "name"), Text(offer, "name"));
            if (product.Length == 0)
                continue;

            var quantity = FirstText(Text(Item(offer, "eligibleQuantity"), "value"), Text(offer, "eligibleQuantity"));
            var price = Money(offer, "price", "priceCurrency");
            lines.Add(new ActionCardLine(product, quantity.Length > 0 && quantity != "1" ? $"{quantity} × {price}" : price));
        }

        var total = FirstText(Money(order, "price", "priceCurrency"), Money(Item(order, "totalPaymentDue"), "price", "priceCurrency"));
        var status = OrderStatus(Text(order, "orderStatus"));

        return new ActionCardModel
        {
            Kind = ActionCardKind.Order,
            Glyph = WinoIconGlyph.Cart,
            Title = Join(" · ", number.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_OrderTitleFormat, number) : Translator.ActionCard_Order, merchant),
            Summary = Join(" · ", lines.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_ItemsFormat, lines.Count) : string.Empty, total, status),
            Lines = lines,
            Total = total,
            Fields = Fields(
                (Translator.ActionCard_Status, status, false),
                (Translator.ActionCard_OrderDate, FormatDate(Moment(order, "orderDate")), false),
                (Translator.ActionCard_Order, number, true)),
            PrimaryAction = SenderLink(order, Translator.ActionCard_ViewOrder, "url")
        };
    }

    private static ActionCardModel? Parcel(MailStructuredItem delivery, string senderName)
    {
        var order = Item(delivery, "partOfOrder");
        var merchant = FirstText(Text(Item(order, "merchant"), "name"), Text(Item(order, "seller"), "name"), senderName);
        var carrier = FirstText(Text(Item(delivery, "carrier"), "name"), Text(Item(delivery, "provider"), "name"));
        var trackingNumber = Text(delivery, "trackingNumber");
        var expected = FirstMoment(delivery, "expectedArrivalUntil", "expectedArrivalFrom");
        var statusText = FirstText(Text(Item(delivery, "deliveryStatus"), "name"), Text(delivery, "deliveryStatus"), Text(order, "orderStatus"));
        var stage = DeliveryStage(statusText);
        var status = OrderStatus(statusText);

        return new ActionCardModel
        {
            Kind = ActionCardKind.Parcel,
            Glyph = WinoIconGlyph.Package,
            Title = string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_ParcelTitleFormat, merchant),
            Summary = Join(" · ", status, expected is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_ExpectedFormat, FormatMoment(expected))),
            Steps = stage < 0 ? [] :
            [
                new ActionCardStep(Translator.ActionCard_StepOrdered, stage >= 0, stage == 0),
                new ActionCardStep(Translator.ActionCard_StepShipped, stage >= 1, stage == 1),
                new ActionCardStep(Translator.ActionCard_StepDelivered, stage >= 2, stage == 2)
            ],
            Fields = Fields(
                (Translator.ActionCard_Expected, FormatMoment(expected), false),
                (Translator.ActionCard_Carrier, carrier, false),
                (Translator.ActionCard_TrackingNumber, trackingNumber, true)),
            PrimaryAction = FirstSenderAction(delivery, "TrackAction", Translator.ActionCard_TrackPackage, "trackingUrl")
                            ?? SenderLink(order, Translator.ActionCard_ViewOrder, "url")
        };
    }

    private static ActionCardModel? Invoice(MailStructuredItem invoice, string senderName)
    {
        var provider = FirstText(Text(Item(invoice, "provider"), "name"), Text(Item(invoice, "broker"), "name"), senderName);
        var amount = FirstText(Money(Item(invoice, "totalPaymentDue"), "price", "priceCurrency"), Money(Item(invoice, "minimumPaymentDue"), "price", "priceCurrency"));
        var due = Moment(invoice, "paymentDueDate");
        var number = FirstText(Text(invoice, "confirmationNumber"), Text(invoice, "identifier"), Text(invoice, "accountId"));
        var title = string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_InvoiceTitleFormat, provider);

        return new ActionCardModel
        {
            Kind = ActionCardKind.Invoice,
            Glyph = WinoIconGlyph.Receipt,
            Title = title,
            Summary = Join(" · ", amount, due is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_DueFormat, FormatDate(due))),
            AmountLabel = amount.Length > 0 ? Translator.ActionCard_AmountDue : string.Empty,
            Amount = amount,
            Fields = Fields(
                (Translator.ActionCard_Due, FormatDate(due), false),
                (Translator.ActionCard_Status, PaymentStatus(Text(invoice, "paymentStatus")), false),
                (Translator.ActionCard_Invoice, number, true)),
            PrimaryAction = FirstSenderAction(invoice, "PayAction", Translator.ActionCard_PayInvoice, "paymentUrl") ?? TaskAction(title, due),
            MoreActions = Actions(TaskAction(title, due), SenderLink(invoice, Translator.ActionCard_ViewInvoice, "url"))
        };
    }

    private static ActionCardModel? Other(MailStructuredItem item, string senderName)
    {
        var fields = new List<ActionCardField>();

        foreach (var property in item.Properties)
        {
            if (fields.Count == MaximumOtherFields)
                break;

            if (property.Name is "name" or "url" or "potentialAction" or "description" or "image")
                continue;

            if (property.Value is MailStructuredItem nested && Text(nested, "name").Length == 0)
            {
                foreach (var nestedProperty in nested.Properties)
                {
                    if (fields.Count < MaximumOtherFields && Scalar(nestedProperty.Value) is { Length: > 0 } nestedValue)
                        fields.Add(new ActionCardField(Label(nestedProperty.Name), nestedValue));
                }

                continue;
            }

            if (Scalar(property.Value) is { Length: > 0 } value)
                fields.Add(new ActionCardField(Label(property.Name), value));
        }

        var name = FirstText(Text(item, "name"), senderName);
        if (fields.Count == 0)
            return null;

        return new ActionCardModel
        {
            Kind = ActionCardKind.Other,
            Glyph = WinoIconGlyph.Tag,
            Title = name,
            Summary = fields[0].Value,
            Fields = fields,
            PrimaryAction = FirstSenderAction(item, null, Translator.ActionCard_Open, "url")
        };
    }

    #endregion

    #region Actions

    private static List<ActionCardAction> Actions(params ActionCardAction?[] actions)
        => actions.OfType<ActionCardAction>().ToList();

    // A schema.org potentialAction of the type (any when null) with a web target, else the first of the named properties.
    private static ActionCardAction? FirstSenderAction(MailStructuredItem? item, string? actionType, string label, params string[] fallbackProperties)
    {
        foreach (var action in Items(Value(item, "potentialAction")))
        {
            if (actionType is not null && TypeName(action) != actionType)
                continue;

            if ((WebAddress(Value(action, "target")) ?? WebAddress(Value(action, "url"))) is { } address)
                return SenderAction(FirstText(Text(action, "name"), label), address);
        }

        return SenderLink(item, label, fallbackProperties);
    }

    private static ActionCardAction? SenderLink(MailStructuredItem? item, string label, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (WebAddress(Value(item, property)) is { } address)
                return SenderAction(label, address);
        }

        return null;
    }

    private static ActionCardAction SenderAction(string label, Uri address) => new()
    {
        Kind = ActionCardActionKind.OpenSenderLink,
        Label = label,
        Glyph = WinoIconGlyph.Open,
        Address = address
    };

    private static ActionCardAction? CalendarAction(string title, DateTimeOffset? start, DateTimeOffset? end, string location, bool allDayWithoutTime = false)
    {
        if (start is not { } startValue)
            return null;

        var startHasTime = HasTime(startValue);
        var isAllDay = allDayWithoutTime && !startHasTime || !startHasTime && end is null;
        var startLocal = Local(startValue);
        var endLocal = end is { } endValue ? Local(endValue) : startLocal.AddHours(1);

        if (endLocal <= startLocal)
            endLocal = isAllDay ? startLocal.AddDays(1) : startLocal.AddHours(1);

        return new ActionCardAction
        {
            Kind = ActionCardActionKind.AddToCalendar,
            Label = Translator.ActionCard_AddToCalendar,
            Glyph = WinoIconGlyph.CalendarAdd,
            Start = startLocal,
            End = endLocal,
            IsAllDay = isAllDay,
            ItemTitle = title,
            Location = location
        };
    }

    private static ActionCardAction? TaskAction(string title, DateTimeOffset? due)
        => due is { } dueValue
            ? new ActionCardAction
            {
                Kind = ActionCardActionKind.CreateTask,
                Label = Translator.ActionCard_CreateTask,
                Glyph = WinoIconGlyph.TaskList,
                Start = dueValue.Date,
                ItemTitle = title
            }
            : null;

    private static Uri? WebAddress(MailStructuredValue? value)
    {
        var text = value switch
        {
            MailStructuredItem entryPoint => FirstText(Text(entryPoint, "urlTemplate"), Text(entryPoint, "url")),
            _ => Scalar(value)
        };

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

    #endregion

    #region Values

    private static string TypeName(MailStructuredItem item) => item.Type;

    private static MailStructuredValue? Value(MailStructuredItem? item, string name)
        => item?.Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal))?.Value;

    private static MailStructuredItem? Item(MailStructuredItem? item, string name) => Items(Value(item, name)).FirstOrDefault();

    private static IEnumerable<MailStructuredItem> Items(MailStructuredValue? value) => value switch
    {
        MailStructuredItem item => [item],
        MailStructuredList list => list.Values.SelectMany(Items),
        _ => []
    };

    private static string Text(MailStructuredItem? item, string name) => Scalar(Value(item, name));

    private static string Scalar(MailStructuredValue? value) => value switch
    {
        MailStructuredText text => EnumerationName(text.Value.Trim()),
        MailStructuredNumber number => number.Value.ToString(CultureInfo.CurrentCulture),
        MailStructuredItem item => Text(item, "name"),
        MailStructuredList list => list.Values.Select(Scalar).FirstOrDefault(static text => text.Length > 0) ?? string.Empty,
        _ => string.Empty
    };

    // Enumeration members are written as their schema.org address ("http://schema.org/OrderProcessing").
    private static string EnumerationName(string text)
    {
        if (text.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase))
            return text[SchemaPrefix.Length..];

        return text.StartsWith(SecureSchemaPrefix, StringComparison.OrdinalIgnoreCase) ? text[SecureSchemaPrefix.Length..] : text;
    }

    private static string FirstText(params string[] values) => values.FirstOrDefault(static value => value.Length > 0) ?? string.Empty;

    private static string Join(string separator, params string[] values)
        => string.Join(separator, values.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string Route(string from, string to) => from.Length > 0 && to.Length > 0 ? $"{from} → {to}" : string.Empty;

    private static string Terminal(MailStructuredItem item, string name)
        => Text(item, name) is { Length: > 0 } terminal ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_TerminalFormat, terminal) : string.Empty;

    private static string Address(MailStructuredItem? place)
    {
        var address = Value(place, "address");
        if (address is MailStructuredItem postal)
            return Join(", ", Text(postal, "streetAddress"), Join(" ", Text(postal, "postalCode"), Text(postal, "addressLocality")), Text(postal, "addressCountry"));

        return Scalar(address);
    }

    private static string Guests(MailStructuredItem reservation)
    {
        var adults = Text(reservation, "numAdults");
        var children = Text(reservation, "numChildren");

        return Join(" · ",
            adults.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_AdultsFormat, adults) : string.Empty,
            children.Length > 0 && children != "0" ? string.Format(CultureInfo.CurrentCulture, Translator.ActionCard_ChildrenFormat, children) : string.Empty);
    }

    private static string Money(MailStructuredItem? item, string priceName, string currencyName)
    {
        var price = Value(item, priceName);
        var currency = Text(item, currencyName);
        var amount = price switch
        {
            MailStructuredNumber number => number.Value,
            MailStructuredText text when double.TryParse(text.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (double?)null
        };

        if (amount is not { } value)
            return Scalar(price);

        var formatted = value.ToString("N2", CultureInfo.CurrentCulture);
        return currency.Length > 0 ? $"{currency} {formatted}" : formatted;
    }

    // A property name as a label: "membershipNumber" reads "Membership number".
    private static string Label(string name)
    {
        if (name.Length == 0)
            return name;

        var builder = new System.Text.StringBuilder(name.Length + 4);
        builder.Append(char.ToUpperInvariant(name[0]));

        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                builder.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else
                builder.Append(name[i]);
        }

        return builder.ToString();
    }

    private static List<ActionCardField> Fields(params (string Label, string Value, bool IsCopyable)[] fields)
        => fields.Where(static field => field.Value.Length > 0)
                 .Select(static field => new ActionCardField(field.Label, field.Value, field.IsCopyable))
                 .ToList();

    private static string OrderStatus(string status) => status switch
    {
        "OrderProcessing" => Translator.ActionCard_StatusProcessing,
        "OrderInTransit" => Translator.ActionCard_StatusInTransit,
        "OrderDelivered" => Translator.ActionCard_StatusDelivered,
        "OrderPickupAvailable" => Translator.ActionCard_StatusPickupAvailable,
        "OrderPaymentDue" => Translator.ActionCard_StatusPaymentDue,
        "OrderProblem" => Translator.ActionCard_StatusProblem,
        "OrderReturned" => Translator.ActionCard_StatusReturned,
        "OrderCancelled" => Translator.ActionCard_StatusCancelled,
        _ => status
    };

    private static int DeliveryStage(string status) => status switch
    {
        "OrderProcessing" or "OrderPaymentDue" => 0,
        "OrderInTransit" or "OrderPickupAvailable" => 1,
        "OrderDelivered" => 2,
        _ => -1
    };

    private static string PaymentStatus(string status) => status switch
    {
        "PaymentDue" => Translator.ActionCard_StatusPaymentDue,
        "PaymentPastDue" => Translator.ActionCard_StatusPastDue,
        "PaymentComplete" or "PaymentAutomaticallyApplied" => Translator.ActionCard_StatusPaid,
        "PaymentDeclined" => Translator.ActionCard_StatusDeclined,
        _ => status
    };

    #endregion

    #region Dates

    private static DateTimeOffset? Moment(MailStructuredItem? item, string name)
    {
        var text = Text(item, name);

        return text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var moment)
            ? moment
            : null;
    }

    private static DateTimeOffset? FirstMoment(MailStructuredItem? item, params string[] names)
        => names.Select(name => Moment(item, name)).FirstOrDefault(static moment => moment is not null);

    // ISO dates without a time ("2026-12-12") parse as midnight.
    private static bool HasTime(DateTimeOffset moment) => moment.TimeOfDay != TimeSpan.Zero;

    // A booking states the local time of the place it is about; the calendar gets that moment in the user's time zone.
    private static DateTime Local(DateTimeOffset moment) => HasTime(moment) ? moment.LocalDateTime : moment.Date;

    private static string FormatDate(DateTimeOffset? moment)
    {
        if (moment is not { } value)
            return string.Empty;

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        return $"{format.GetAbbreviatedDayName(value.DayOfWeek)}, {value.DateTime.ToString(format.MonthDayPattern, CultureInfo.CurrentCulture)}";
    }

    // The time as the sender wrote it: a departure is shown in the airport's own time.
    private static string FormatTime(DateTimeOffset? moment)
        => moment is { } value && HasTime(value) ? value.DateTime.ToString("t", CultureInfo.CurrentCulture) : string.Empty;

    private static string FormatMoment(DateTimeOffset? moment) => Join(", ", FormatDate(moment), FormatTime(moment));

    private static string MomentSpan(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null)
            return string.Empty;

        var startText = FormatMoment(start);
        if (end is not { } endValue)
            return startText;

        return endValue.Date == start.Value.Date ? $"{startText} – {FormatTime(end)}" : $"{startText} – {FormatMoment(end)}";
    }

    private static string DateSpan(DateTimeOffset? start, DateTimeOffset? end)
        => start is null ? string.Empty : end is null ? FormatDate(start) : $"{FormatDate(start)} – {FormatDate(end)}";

    #endregion
}
