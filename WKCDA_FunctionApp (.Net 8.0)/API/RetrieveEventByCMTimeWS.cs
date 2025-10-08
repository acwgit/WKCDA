using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class RetrieveEventsByTimeWS : WS_Base
{
    public RetrieveEventsByTimeWS(ILogger<RetrieveEventsByTimeWS> logger) : base(logger) { }

    [Function("RetrieveEventsByTimeWS")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveEventsByTimeWS")] HttpRequest req)
    {
        _logger.LogInformation("RetrieveEventsByTimeWS triggered.");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            if (requestBody?.eventListByTime == null)
            {
                return new OkObjectResult(new ResponseBody_Item
                {
                    Success = false,
                    Remarks = "Invalid input: eventListByTime is required"
                });
            }

            var events = GetEvents(svc, requestBody);

            return new OkObjectResult(new ResponseBody_Item
            {
                Success = true,
                Remarks = null,
                Events = events
            });
        }
        catch (JsonException)
        {
            return new OkObjectResult(new ResponseBody_Item
            {
                Success = false,
                Remarks = "Invalid Json"
            });
        }
        catch (AuthenticationException ex)
        {
            return new UnauthorizedObjectResult(ex.Message);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("unauthorized") || ex.Message.Contains("authorization failed"))
            {
                return new ObjectResult(new
                {
                    error = "Session expired or invalid",
                    message = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
            else
            {
                return new ObjectResult(new
                {
                    error = "Bad Request",
                    message = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }

        }
    }

    private const string CrmEntity = "msevtmgt_event";

    private List<ResponseBody_Event> GetEvents(ServiceClient svc, RequestBody body)
    {
        var qe = new QueryExpression(CrmEntity)
        {
            ColumnSet = GetColumnSetFromMapping()
        };

        /*// Filter on EventStatus
        if (!string.IsNullOrEmpty(body.eventListByTime.EventStatus))
        {
            var statuses = body.eventListByTime.EventStatus
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(label => CRMEntityHelper.getOptionSetValue(svc, CrmEntity, "wkcda_eventstatus", label.Trim(), _logger))
                .Where(v => v.HasValue)
                .Select(v => (object)v.Value)
                .ToArray();

            if (statuses.Any())
                qe.Criteria.AddCondition("wkcda_eventstatus", ConditionOperator.In, statuses);
        }

        // Filter on RecordType
        if (!string.IsNullOrEmpty(body.eventListByTime.RecordType))
        {
            var types = body.eventListByTime.RecordType
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(label => CRMEntityHelper.getOptionSetValue(svc, CrmEntity, "wkcda_recordtype", label.Trim(), _logger))
                .Where(v => v.HasValue)
                .Select(v => (object)v.Value)
                .ToArray();

            if (types.Any())
                qe.Criteria.AddCondition("wkcda_recordtype", ConditionOperator.In, types);
        }*/


        // Date filter
        if (DateTime.TryParse(body.eventListByTime.FromDateTime, out var fromDate) &&
            DateTime.TryParse(body.eventListByTime.ToDateTime, out var toDate))
        {
            var dateFilter = new FilterExpression(LogicalOperator.And);
            dateFilter.AddCondition("msevtmgt_eventstartdate", ConditionOperator.OnOrAfter, fromDate);
            dateFilter.AddCondition("wkcda_eventenddate", ConditionOperator.OnOrBefore, toDate);

            qe.Criteria.AddFilter(dateFilter);
        }

        // Sorting
        /*if (!string.IsNullOrEmpty(body.eventListByTime.RetrieveEventBy))
        {
            if (body.eventListByTime.RetrieveEventBy.Equals("LastCreatedTime", StringComparison.OrdinalIgnoreCase))
                qe.Orders.Add(new OrderExpression("createdon", OrderType.Descending));
            else if (body.eventListByTime.RetrieveEventBy.Equals("LastModifiedTime", StringComparison.OrdinalIgnoreCase))
                qe.Orders.Add(new OrderExpression("modifiedon", OrderType.Descending));
        }*/

        var entities = svc.RetrieveMultiple(qe).Entities;
        var mappings = GetMappings();

        return entities.Select(e =>
        {
            string eventStatus = GetOptionSetLabel(e, "wkcda_eventstatus", svc) ?? string.Empty;
            string recordType = GetOptionSetLabel(e, "wkcda_recordtype", svc) ?? string.Empty;
            string organizedBy = GetOptionSetLabel(e, "wkcda_organizedby", svc) ?? string.Empty;
            return new ResponseBody_Event
            {
                EventCode = e.GetAttributeValue<string>(mappings["EventCode"]),
                EventNameEng = e.GetAttributeValue<string>(mappings["EventNameEng"]),
                EventNameTC = e.GetAttributeValue<string>(mappings["EventNameTC"]),
                EventNameSC = e.GetAttributeValue<string>(mappings["EventNameSC"]),
                EventStatus = eventStatus,
                RecordType = recordType,
                Quota = e.GetAttributeValue<double?>("wkcda_quota") ?? 0.0,
                Vacancy = e.GetAttributeValue<double?>("wkcda_vacancy") ?? 0.0,
                TotalAccepted = e.GetAttributeValue<double?>("wkcda_totalacceptedparticipant") ?? 0,
                TotalRejected = e.GetAttributeValue<double?>("wkcda_totalrejecteddeclinedparticipant") ?? 0,
                OpenForRegistration = e.GetAttributeValue<bool?>("wkcda_openforregistration") ?? false,
                RegistrationOpeningTime = e.GetAttributeValue<DateTime?>("wkcda_registrationopeningtime"),
                RegistrationClosingTime = e.GetAttributeValue<DateTime?>("wkcda_registrationclosingtime"),
                EventStartDate = e.GetAttributeValue<DateTime?>("msevtmgt_eventstartdate"),
                EventEndDate = e.GetAttributeValue<DateTime?>("wkcda_eventenddate"),
                EventVenueName = e.GetAttributeValue<EntityReference>("wkcda_eventvenue"),
                OrganizedBy = organizedBy,

                // Default/null fields
                QuestionRemark1 = null,
                QuestionRemark1TC = null,
                QuestionRemark1SC = null,
                QuestionRemark2 = null,
                QuestionRemark2TC = null,
                QuestionRemark2SC = null,
                QuestionRemark3 = null,
                QuestionRemark3TC = null,
                QuestionRemark3SC = null,
                QuestionRemark4 = null,
                QuestionRemark4TC = null,
                QuestionRemark4SC = null,
                QuestionRemark5 = null,
                QuestionRemark5TC = null,
                QuestionRemark5SC = null,
                QuestionRemark6 = null,
                QuestionRemark6TC = null,
                QuestionRemark6SC = null,
                QuestionRemark7 = null,
                QuestionRemark7TC = null,
                QuestionRemark7SC = null,
                QuestionRemark8 = null,
                QuestionRemark8TC = null,
                QuestionRemark8SC = null,
                QuestionRemark9 = null,
                QuestionRemark9TC = null,
                QuestionRemark9SC = null,
                QuestionRemark10 = null,
                QuestionRemark10TC = null,
                QuestionRemark10SC = null,
                TotalParticipantPendingforScreening = 0,
                TotalParticipantinWaitingList = 0,
                TotalNumberofRespondent = 0,
                ScreeningMode = null,
                RemainingAvailabilityforAutoAccept = -1,
                EventStartTimeHour = e.GetAttributeValue<DateTime?>("msevtmgt_eventstartdate")?.ToString("HH"),
                EventStartTimeMinute = e.GetAttributeValue<DateTime?>("msevtmgt_eventstartdate")?.ToString("mm"),
                EventEndTimeHour = e.GetAttributeValue<DateTime?>("wkcda_eventenddate")?.ToString("HH"),
                EventEndTimeMinute = e.GetAttributeValue<DateTime?>("wkcda_eventenddate")?.ToString("mm"),
                Availability = null,
                AutoAcceptThreshold = 0
            };
        }).ToList();
    }

    protected override Dictionary<string, string> GetMappings()
    {
        return new Dictionary<string, string>
        {
            { "EventCode", "wkcda_eventcode" },
            { "EventNameEng", "wkcda_eventname_eng" },
            { "EventNameTC", "wkcda_eventname_tc" },
            { "EventNameSC", "wkcda_eventname_sc" },
            { "EventStatus", "wkcda_eventstatus" },
            { "RecordType", "wkcda_recordtype" },
            { "Quota", "wkcda_quota" },
            { "Vacancy", "wkcda_vacancy" },
            { "TotalAccepted", "wkcda_totalacceptedparticipant" },
            { "TotalRejected", "wkcda_totalrejecteddeclinedparticipant" },
            { "OpenForRegistration", "wkcda_openforregistration" },
            { "EventStartDate", "msevtmgt_eventstartdate" },
            { "EventEndDate", "wkcda_eventenddate" },
            { "RegistrationOpeningTime", "wkcda_registrationopeningtime" },
            { "RegistrationClosingTime", "wkcda_registrationclosingtime" },
            { "EventVenueName", "wkcda_eventvenue" },
            { "OrganizedBy", "wkcda_organizedby" }
        };
    }
    private string? GetOptionSetLabel(Entity e, string fieldName, ServiceClient svc)
    {
        if (!e.Attributes.Contains(fieldName)) return null;
        var val = e[fieldName] as OptionSetValue;
        if (val == null) return null;

        return CRMEntityHelper.getOptionSetLabel(svc, CrmEntity, fieldName, val.Value, _logger);
    }
    private ColumnSet GetColumnSetFromMapping()
    {
        return new ColumnSet(GetMappings().Values.ToArray());
    }

    #region Request Classes
    public class RequestBody
    {
        public required RequestBody_Event eventListByTime { get; set; }
    }

    public class RequestBody_Event
    {
        public string FromDateTime { get; set; }
        public string ToDateTime { get; set; }
        public string RetrieveEventBy { get; set; }
        public string EventStatus { get; set; }
        public string RecordType { get; set; }
    }
    #endregion

    #region Response Classes
    public class ResponseBody_Event
    {
        public double Vacancy { get; set; }
        public double TotalRejected { get; set; }
        public int TotalParticipantPendingforScreening { get; set; }
        public int TotalParticipantinWaitingList { get; set; }
        public int TotalNumberofRespondent { get; set; }
        public double TotalAccepted { get; set; }
        public string? ScreeningMode { get; set; }
        public string? EventStatus { get; set; }
        public int RemainingAvailabilityforAutoAccept { get; set; }
        public DateTime? RegistrationOpeningTime { get; set; }
        public DateTime? RegistrationClosingTime { get; set; }
        public string? RecordType { get; set; }
        public double Quota { get; set; }
        public string? QuestionRemark1 { get; set; }
        public string? QuestionRemark1TC { get; set; }
        public string? QuestionRemark1SC { get; set; }
        public string? QuestionRemark2 { get; set; }
        public string? QuestionRemark2TC { get; set; }
        public string? QuestionRemark2SC { get; set; }
        public string? QuestionRemark3 { get; set; }
        public string? QuestionRemark3TC { get; set; }
        public string? QuestionRemark3SC { get; set; }
        public string? QuestionRemark4 { get; set; }
        public string? QuestionRemark4TC { get; set; }
        public string? QuestionRemark4SC { get; set; }
        public string? QuestionRemark5 { get; set; }
        public string? QuestionRemark5TC { get; set; }
        public string? QuestionRemark5SC { get; set; }
        public string? QuestionRemark6 { get; set; }
        public string? QuestionRemark6TC { get; set; }
        public string? QuestionRemark6SC { get; set; }
        public string? QuestionRemark7 { get; set; }
        public string? QuestionRemark7TC { get; set; }
        public string? QuestionRemark7SC { get; set; }
        public string? QuestionRemark8 { get; set; }
        public string? QuestionRemark8TC { get; set; }
        public string? QuestionRemark8SC { get; set; }
        public string? QuestionRemark9 { get; set; }
        public string? QuestionRemark9TC { get; set; }
        public string? QuestionRemark9SC { get; set; }
        public string? QuestionRemark10 { get; set; }
        public string? QuestionRemark10TC { get; set; }
        public string? QuestionRemark10SC { get; set; }
        public string? QuestionRemark11 { get; set; }
        public string? QuestionRemark11TC { get; set; }
        public string? QuestionRemark11SC { get; set; }
        public string? QuestionRemark12 { get; set; }
        public string? QuestionRemark12TC { get; set; }
        public string? QuestionRemark12SC { get; set; }
        public string? QuestionRemark13 { get; set; }
        public string? QuestionRemark13TC { get; set; }
        public string? QuestionRemark13SC { get; set; }
        public string? QuestionRemark14 { get; set; }
        public string? QuestionRemark14TC { get; set; }
        public string? QuestionRemark14SC { get; set; }
        public string? QuestionRemark15 { get; set; }
        public string? QuestionRemark15TC { get; set; }
        public string? QuestionRemark15SC { get; set; }
        public string? QuestionRemark16 { get; set; }
        public string? QuestionRemark16TC { get; set; }
        public string? QuestionRemark16SC { get; set; }
        public string? QuestionRemark17 { get; set; }
        public string? QuestionRemark17TC { get; set; }
        public string? QuestionRemark17SC { get; set; }
        public string? QuestionRemark18 { get; set; }
        public string? QuestionRemark18TC { get; set; }
        public string? QuestionRemark18SC { get; set; }
        public string? QuestionRemark19 { get; set; }
        public string? QuestionRemark19TC { get; set; }
        public string? QuestionRemark19SC { get; set; }
        public string? QuestionRemark20 { get; set; }
        public string? QuestionRemark20TC { get; set; }
        public string? QuestionRemark20SC { get; set; }
        public bool OpenForRegistration { get; set; }
        public EntityReference? EventVenueName { get; set; }
        public string? OrganizedBy { get; set; }
        public string? EventNameEng { get; set; }
        public string? EventNameTC { get; set; }
        public string? EventNameSC { get; set; }
        public DateTime? EventStartDate { get; set; }
        public DateTime? EventEndDate { get; set; }
        public string? EventStartTimeHour { get; set; }
        public string? EventStartTimeMinute { get; set; }
        public string? EventEndTimeHour { get; set; }
        public string? EventEndTimeMinute { get; set; }
        public string? EventCode { get; set; }
        public string? Availability { get; set; }
        public int AutoAcceptThreshold { get; set; }
        public string? EventType { get; set; }
        public string? EventRegistrationSiteURL { get; set; }
        public string? EventRegistrationSiteURLTC { get; set; }
        public string? EventRegistrationSiteURLSC { get; set; }
        public string? Department { get; set; }
    }

    public class ResponseBody_Item
    {
        public bool Success { get; set; }
        public string? Remarks { get; set; }
        public List<ResponseBody_Event>? Events { get; set; }
    }
}
#endregion