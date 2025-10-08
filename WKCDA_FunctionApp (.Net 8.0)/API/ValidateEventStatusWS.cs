using Microsoft.AspNetCore.Http;
using DataverseModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using System.Security.Authentication;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class ValidateEventStatusWS : WS_Base
    {
        public ValidateEventStatusWS(ILogger<ValidateEventStatusWS> logger) : base(logger) { }

        [Function("ValidateEventStatusWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/ValidateEventStatusWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: ValidateEventStatusWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.EventList == null || requestBody.EventList.Count == 0)
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid request body or empty event list" } });

                var response = new List<ResponseBody_Item>();

                foreach (var eventItem in requestBody.EventList)
                {
                    if (string.IsNullOrWhiteSpace(eventItem.EventCode))
                    {
                        response.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Event code is required",
                            EventCode = eventItem.EventCode
                        });
                        continue;
                    }

                    var eventValidationResult = ValidateAndGetEvent(svc, eventItem.EventCode);
                    if (!eventValidationResult.IsValid)
                    {
                        response.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = eventValidationResult.Remarks,
                            EventCode = eventItem.EventCode
                        });
                        continue;
                    }

                    response.Add(eventValidationResult.EventData);
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" } });
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

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region CRM Helpers

        private EventValidationResult ValidateAndGetEvent(ServiceClient svc, string eventCode)
        {
            var result = new EventValidationResult();

            try
            {
                _logger.LogInformation($"Looking up event by EventCode: {eventCode}");

                var eventQuery = new QueryExpression("msevtmgt_event")
                {
                    ColumnSet = new ColumnSet(
                        "msevtmgt_eventid",
                        "wkcda_eventcode",
                        "wkcda_eventname_eng",
                        "wkcda_eventname_sc",
                        "wkcda_eventname_tc",
                        "msevtmgt_eventstartdate",
                        "msevtmgt_eventenddate",
                        "wkcda_eventstarttimehour",
                        "wkcda_eventendtimehour",
                        "wkcda_alldayevent",
                        "wkcda_registrationopeningtime",
                        "wkcda_registrationclosingtime",
                        "wkcda_openforregistration",
                        "wkcda_availability",
                        "wkcda_screeningmode",
                        "wkcda_quota",
                        "wkcda_autoacceptthreshold",
                        "wkcda_remainingavailabilityforautoaccept",
                        "wkcda_totalacceptedparticipant",
                        "wkcda_totalrejecteddeclinedparticipant",
                        "wkcda_totalnumberofrespondent",
                        "wkcda_totalparticipantpendingforscreening",
                        "wkcda_totalparticipantinwaitinglist",
                        "wkcda_vacancy",
                        //"wkcda_sessioncode",
                        "msevtmgt_marketingformid",
                        "statuscode"
                    ),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_eventcode", ConditionOperator.Equal, eventCode)
                        }
                    }
                };

                var eventResult = svc.RetrieveMultiple(eventQuery);
                if (eventResult.Entities.Count > 0)
                {
                    var eventEntity = eventResult.Entities[0];

                    result.IsValid = true;
                    result.EventData = new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = null,
                        EventCode = eventEntity.GetAttributeValue<string>("wkcda_eventcode"),
                        EventValidation = true,
                        EventStatus = GetEventStatusText(eventEntity.GetAttributeValue<OptionSetValue>("statuscode")),
                        EventName = eventEntity.GetAttributeValue<string>("wkcda_eventname_eng"),
                        EventNameSC = eventEntity.GetAttributeValue<string>("wkcda_eventname_sc"),
                        EventNameTC = eventEntity.GetAttributeValue<string>("wkcda_eventname_tc"),
                        EventStartDate = FormatDate(eventEntity.GetAttributeValue<DateTime?>("msevtmgt_eventstartdate")),
                        EventEndDate = FormatDate(eventEntity.GetAttributeValue<DateTime?>("msevtmgt_eventenddate")),
                        EventStartTime = GetEventStatusText(eventEntity.GetAttributeValue<OptionSetValue>("wkcda_eventstarttimehour")),
                        EventEndTime = GetEventStatusText(eventEntity.GetAttributeValue<OptionSetValue>("wkcda_eventendtimehour")),
                        AllDayEvent = eventEntity.GetAttributeValue<bool?>("wkcda_alldayevent") ?? false,
                        RegistrationOpeningTime = FormatDateTime(eventEntity.GetAttributeValue<DateTime?>("wkcda_registrationopeningtime")),
                        RegistrationClosingTime = FormatDateTime(eventEntity.GetAttributeValue<DateTime?>("wkcda_registrationclosingtime")),
                        OpenForRegistration = eventEntity.GetAttributeValue<bool?>("wkcda_openforregistration") ?? false,
                        Availability = eventEntity.GetAttributeValue<string>("wkcda_availability"),
                        ScreeningMode = GetOptionSetText(eventEntity, "wkcda_screeningmode"),
                        Quota = eventEntity.GetAttributeValue<Double?>("wkcda_quota"),
                        AutoAcceptThreshold = eventEntity.GetAttributeValue<Double?>("wkcda_autoacceptthreshold"),
                        RemainingAvailabilityforAutoAccept = eventEntity.GetAttributeValue<Double?>("wkcda_remainingavailabilityforautoaccept"),
                        TotalAcceptedParticipant = eventEntity.GetAttributeValue<Double?>("wkcda_totalacceptedparticipant"),
                        TotalDeclinedParticipant = eventEntity.GetAttributeValue<Double?>("wkcda_totalrejecteddeclinedparticipant"),
                        TotalNumberofRespondent = eventEntity.GetAttributeValue<Double?>("wkcda_totalnumberofrespondent"),
                        TotalParticipantPendingforScreening = eventEntity.GetAttributeValue<Double?>("wkcda_totalparticipantpendingforscreening"),
                        TotalParticipantinWaitingList = eventEntity.GetAttributeValue<Double?>("wkcda_totalparticipantinwaitinglist"),
                        Vacancy = eventEntity.GetAttributeValue<Double?>("wkcda_vacancy"),
                        SessionCode = eventEntity.GetAttributeValue<string>("wkcda_sessioncode"),
                        FormId = eventEntity.GetAttributeValue<EntityReference>("msevtmgt_marketingformid")?.Id.ToString()


                    };

                    _logger.LogInformation($"Found event: Code={eventCode}, Name={result.EventData.EventName}, Status={result.EventData.EventStatus}");
                }
                else
                {
                    result.IsValid = false;
                    result.Remarks = $"No event found with EventCode: {eventCode}";
                    _logger.LogWarning(result.Remarks);
                }

                return result;
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError(ex, "Authentication failed while validating event.");

                return new EventValidationResult
                {
                    IsValid = false,
                    Remarks = $"Authentication failed: {ex.Message}",
                    EventData = new ResponseBody_Item
                    {
                        Success = false,
                        Remarks = ex.Message,
                        EventValidation = false
                    }
                };
            }

        }

        private string GetEventStatusText(OptionSetValue statusCode)
        {
            if (statusCode == null) return "Unknown";

            return statusCode.Value switch
            {
                1 => "Active",
                2 => "Inactive",
                200000 => "Established",
                200001 => "Completed",
                200002 => "Cancelled",
                _ => statusCode.Value.ToString()
            };
        }

        private string GetOptionSetText(Entity entity, string attributeName, string defaultText = null)
        {
            var optionSetValue = entity.GetAttributeValue<OptionSetValue>(attributeName);
            if (optionSetValue != null)
            {
                return entity.FormattedValues.ContainsKey(attributeName)
                    ? entity.FormattedValues[attributeName]
                    : optionSetValue.Value.ToString();
            }
            return defaultText;
        }

        private string FormatDate(DateTime? date)
        {
            return date?.ToString("yyyy-MM-dd");
        }

        private string FormatTime(string time)
        {
            if (string.IsNullOrWhiteSpace(time)) return null;

            if (TimeSpan.TryParse(time, out var timeSpan))
            {
                return timeSpan.ToString(@"hh\:mm");
            }

            return time;
        }

        private string FormatDateTime(DateTime? dateTime)
        {
            return dateTime?.ToString("yyyy-MM-dd HH:mm");
        }

        #endregion

        #region Helper Classes

        private class EventValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
            public ResponseBody_Item EventData { get; set; }
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public List<EventRequestItem> EventList { get; set; }
        }

        public class EventRequestItem
        {
            public string EventCode { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string EventCode { get; set; }
            public bool EventValidation { get; set; }
            public string EventStatus { get; set; }
            public string EventName { get; set; }
            public string EventNameSC { get; set; }
            public string EventNameTC { get; set; }
            public string EventStartDate { get; set; }
            public string EventEndDate { get; set; }
            public string EventStartTime { get; set; }
            public string EventEndTime { get; set; }
            public bool AllDayEvent { get; set; }
            public string RegistrationOpeningTime { get; set; }
            public string RegistrationClosingTime { get; set; }
            public bool OpenForRegistration { get; set; }
            public string Availability { get; set; }
            public string ScreeningMode { get; set; }
            public Double? Quota { get; set; }
            public Double? AutoAcceptThreshold { get; set; }
            public Double? RemainingAvailabilityforAutoAccept { get; set; }
            public Double? TotalAcceptedParticipant { get; set; }
            public Double? TotalDeclinedParticipant { get; set; }
            public Double? TotalNumberofRespondent { get; set; }
            public Double? TotalParticipantPendingforScreening { get; set; }
            public Double? TotalParticipantinWaitingList { get; set; }
            public Double? Vacancy { get; set; }
            public string SessionCode { get; set; }
            public string FormId { get; set; }
        }

        #endregion
    }
}