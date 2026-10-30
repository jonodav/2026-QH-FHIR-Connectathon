using System.Globalization;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using NHapi.Base.Model;
using NHapi.Base.Parser;
using NHapi.Model.V231.Message;

public sealed class FhirOrmConverter
{
    private readonly PipeParser _pipeParser = new();

    public ORM_O01 ConvertJson(string bundleJson)
    {
        var parser = new FhirJsonParser();

        if (parser.Parse<Bundle>(bundleJson) is not Bundle bundle)
            throw new InvalidOperationException("FHIR JSON is not a Bundle.");

        return Convert(bundle);
    }

    public ORM_O01 Convert(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var patient = bundle.Entry
            .Select(x => x.Resource)
            .OfType<Patient>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The Bundle must contain exactly one Patient.");

        var serviceRequests = bundle.Entry
            .Select(x => x.Resource)
            .OfType<ServiceRequest>()
            .ToList();

        if (serviceRequests.Count == 0)
            throw new InvalidOperationException(
                "The Bundle does not contain any ServiceRequest resources.");

        var message = new ORM_O01();

        PopulateMsh(message, bundle);
        PopulatePid(message, patient);

        for (var index = 0; index < serviceRequests.Count; index++)
            PopulateOrder(message, serviceRequests[index], index);

        return message;
    }

    private static void PopulateMsh(ORM_O01 message, Bundle bundle)
    {
        var msh = message.MSH;

        msh.FieldSeparator.Value = "|";
        msh.EncodingCharacters.Value = "^~\\&";

        msh.SendingApplication.NamespaceID.Value = "FHIR-CONVERTER";
        msh.SendingFacility.NamespaceID.Value = "FHIR-SERVER";
        msh.ReceivingApplication.NamespaceID.Value = "LIS";
        msh.ReceivingFacility.NamespaceID.Value = "LAB";

        msh.DateTimeOfMessage.TimeOfAnEvent.Value =
            DateTimeOffset.Now.ToString("yyyyMMddHHmmsszzz")
                .Replace(":", string.Empty);

        msh.MessageType.MessageType.Value = "ORM";
        msh.MessageType.TriggerEvent.Value = "O01";

        msh.MessageControlID.Value =
            FirstNonEmpty(bundle.Identifier?.Value, bundle.Id, Guid.NewGuid().ToString("N"));

        msh.ProcessingID.ProcessingID.Value = "P";
        msh.VersionID.VersionID.Value = "2.3.1";
    }

    private static void PopulatePid(ORM_O01 message, Patient patient)
    {
        var pid = message.PATIENT.PID;

        pid.SetIDPID.Value = "1";

        var identifier = SelectPatientIdentifier(patient);

        if (identifier is not null)
        {
            var patientId = pid.GetPatientIdentifierList(0);
            patientId.ID.Value = identifier.Value;
            patientId.AssigningAuthority.NamespaceID.Value =
                GetIdentifierNamespace(identifier.System);
        }

        var name = patient.Name.FirstOrDefault();

        if (name is not null)
        {
            var patientName = pid.GetPatientName(0);
            patientName.FamilyLastName.FamilyName.Value = name.Family;
            patientName.GivenName.Value = name.Given.FirstOrDefault();
            patientName.MiddleInitialOrName.Value = name.Given.Skip(1).FirstOrDefault();
            patientName.PrefixEgDR.Value = name.Prefix.FirstOrDefault();
            patientName.SuffixEgJRorIII.Value = name.Suffix.FirstOrDefault();
        }

        if (patient.BirthDate is not null &&
            DateTime.TryParse(
                patient.BirthDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var birthDate))
        {
            pid.DateTimeOfBirth.TimeOfAnEvent.Value =
                 birthDate.ToString("yyyyMMdd");
        }

        pid.Sex.Value = patient.Gender switch
        {
            AdministrativeGender.Male => "M",
            AdministrativeGender.Female => "F",
            AdministrativeGender.Other => "O",
            AdministrativeGender.Unknown => "U",
            _ => "U"
        };

        var address = patient.Address.FirstOrDefault();

        if (address is not null)
        {
            var patientAddress = pid.GetPatientAddress(0);
            patientAddress.StreetAddress.Value = address.Line.FirstOrDefault();
            patientAddress.OtherDesignation.Value = address.Line.Skip(1).FirstOrDefault();
            patientAddress.City.Value = address.City;
            patientAddress.StateOrProvince.Value = address.State;
            patientAddress.ZipOrPostalCode.Value = address.PostalCode;
            patientAddress.Country.Value = address.Country;
        }

        var phone = patient.Telecom.FirstOrDefault(x =>
            x.System == ContactPoint.ContactPointSystem.Phone);

        if (phone is not null)
            pid.GetPhoneNumberHome(0).PhoneNumber.Value = phone.Value;
    }

    private static void PopulateOrder(
        ORM_O01 message,
        ServiceRequest request,
        int index)
    {
        var order = message.GetORDER(index);
        var orc = order.ORC;
        var obr = order.ORDER_DETAIL.OBR;

        var placerOrderNumber = FirstNonEmpty(
            request.Identifier
                .FirstOrDefault(x => x.Type?.Coding
                    .Any(c => c.Code == "PLAC") == true)
                ?.Value,
            request.Identifier.FirstOrDefault()?.Value,
            request.Id,
            Guid.NewGuid().ToString("N"));

        orc.OrderControl.Value = MapOrderControl(request.Status);

        orc.PlacerOrderNumber.EntityIdentifier.Value =
            placerOrderNumber;

        orc.PlacerOrderNumber.NamespaceID.Value =
            "FHIR";

        orc.OrderStatus.Value = MapOrderStatus(request.Status);

        if (DateTimeOffset.TryParse(
        request.AuthoredOn,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeLocal,
        out var authoredOn))
        {
            orc.DateTimeOfTransaction.TimeOfAnEvent.Value =
                authoredOn.ToString("yyyyMMddHHmmsszzz")
                    .Replace(":", string.Empty);
        }

        obr.SetIDOBR.Value = (index + 1).ToString();

        obr.PlacerOrderNumber.EntityIdentifier.Value =
            placerOrderNumber;

        var service = obr.UniversalServiceID;

        service.Identifier.Value =
            request.Code?.Coding.FirstOrDefault()?.Code
            ?? request.Code?.Text
            ?? "UNKNOWN";

        service.Text.Value =
            request.Code?.Text
            ?? request.Code?.Coding.FirstOrDefault()?.Display
            ?? "Unknown test";

        service.NameOfCodingSystem.Value =
            GetCodingSystemName(
                request.Code?.Coding.FirstOrDefault()?.System);

        var occurrenceDate = GetOccurrenceDate(request);

        if (occurrenceDate.HasValue)
        {
            obr.RequestedDateTime.TimeOfAnEvent.Value =
                occurrenceDate.Value.ToString("yyyyMMddHHmmss");
        }

        obr.ResultStatus.Value = MapResultStatus(request.Status);

        PopulateOrderingProvider(request, orc);
        PopulateNotes(request, order);
    }

    private static void PopulateOrderingProvider(
        ServiceRequest request,
        NHapi.Model.V231.Segment.ORC orc)
    {
        if (request.Requester is null)
            return;

        var id = request.Requester.Reference?
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        var provider = orc.GetOrderingProvider(0);

        provider.IDNumber.Value = id;
        provider.FamilyLastName.FamilyName.Value = request.Requester.Display;
    }

    private static void PopulateNotes(
        ServiceRequest request,
        NHapi.Model.V231.Group.ORM_O01_ORDER order)
    {
        var notes = request.Note
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();

        for (var index = 0; index < notes.Count; index++)
        {
            var nte = order.ORDER_DETAIL.GetNTE(index);

            nte.SetIDNTE.Value =
                (index + 1).ToString();

            nte.GetComment(0).Value = notes[index].Text;
        }
    }

    private static Identifier? SelectPatientIdentifier(Patient patient)
    {
        return patient.Identifier.FirstOrDefault(x =>
                   x.Use == Identifier.IdentifierUse.Official)
               ?? patient.Identifier.FirstOrDefault();
    }

    private static DateTimeOffset? GetOccurrenceDate(
    ServiceRequest request)
    {
        var value = request.Occurrence switch
        {
            FhirDateTime dateTime => dateTime.Value,
            Period period => period.Start,
            _ => request.AuthoredOn
        };

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var result)
                ? result
                : null;
    }

    private static string MapOrderControl(
        RequestStatus? status)
    {
        return status switch
        {
            RequestStatus.Revoked => "CA",
            RequestStatus.Completed => "SC",
            _ => "NW"
        };
    }

    private static string MapOrderStatus(
        RequestStatus? status)
    {
        return status switch
        {
            RequestStatus.Draft => "HD",
            RequestStatus.Active => "IP",
            RequestStatus.OnHold => "HD",
            RequestStatus.Revoked => "CA",
            RequestStatus.Completed => "CM",
            RequestStatus.EnteredInError => "CA",
            _ => string.Empty
        };
    }

    private static string MapResultStatus(
        RequestStatus? status)
    {
        return status switch
        {
            RequestStatus.Completed => "F",
            RequestStatus.Revoked => "X",
            _ => "O"
        };
    }

    private static string GetIdentifierNamespace(string system)
    {
        if (Uri.TryCreate(system, UriKind.Absolute, out var uri))
            return uri.Segments.LastOrDefault()?.Trim('/')
                   ?? uri.Host;

        return system;
    }

    private static string GetCodingSystemName(string? system)
    {
        if (string.IsNullOrWhiteSpace(system))
            return string.Empty;

        return system switch
        {
            "http://loinc.org" => "LN",
            "http://snomed.info/sct" => "SCT",
            _ => GetIdentifierNamespace(system)
        };
    }

    private static string FirstNonEmpty(
        params string?[] values)
    {
        return values.First(x => !string.IsNullOrWhiteSpace(x))!;
    }
}