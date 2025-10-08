using Microsoft.AspNetCore.Http;
using DataverseModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using Microsoft.Xrm.Sdk.Query;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class TicketingAdmissionWS : WS_Base
    {
        public TicketingAdmissionWS(ILogger<TicketingAdmissionWS> logger) : base(logger) { }

        [Function("TicketingAdmissionWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "services/apexrest/WKCDA/TicketingAdmissionWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: TicketingAdmissionWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.ticketingadmiss == null || requestBody.ticketingadmiss.Count == 0)
                    return new BadRequestObjectResult(new List<SaveResult> {
                new SaveResult { Success = false, Remarks = "Invalid request body - no ticketing admissions provided" }
            });

                var response = await ProcessTicketingAdmissions(svc, requestBody.ticketingadmiss);

                if (response.Any(r => !r.Success))
                {
                    return new BadRequestObjectResult(response);
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new BadRequestObjectResult(new List<SaveResult> {
            new SaveResult { Success = false, Remarks = "Invalid JSON" }
        });
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed");
                return new ObjectResult(new { error = "Unauthorized", message = "Authentication failed" })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
            catch (Exception ex)
            {
                return new ObjectResult(new { error = "Bad Request", message = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
        }

        private bool IsUnauthorizedException(Exception ex)
        {
            if (ex == null) return false;

            if (ex is UnauthorizedAccessException)
                return true;

            var message = ex.Message.ToLowerInvariant();
            return message.Contains("unauthorized") ||
                   message.Contains("authorization failed") ||
                   message.Contains("authentication failed") ||
                   message.Contains("http request is unauthorized") ||
                   message.Contains("bearer authorization_uri") ||
                   ex.InnerException != null && IsUnauthorizedException(ex.InnerException);
        }

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region CRM Helpers

        private async Task<List<SaveResult>> ProcessTicketingAdmissions(ServiceClient svc, List<ticketingadmiss> ticketingAdmissions)
        {
            var resultList = new List<SaveResult>();
            var entitiesToCreate = new List<Entity>();

            foreach (var adm in ticketingAdmissions)
            {
                try
                {
                    var validationResult = await ValidateTicketingAdmission(adm, svc);
                    if (!validationResult.IsValid)
                    {
                        resultList.Add(new SaveResult { Success = false, Remarks = validationResult.Remarks });
                        continue;
                    }

                    var newAdm = await CreateTicketingAdmissionEntity(adm, svc);
                    entitiesToCreate.Add(newAdm);
                    resultList.Add(new SaveResult { Success = true, Remarks = null });
                }
                catch (Exception ex) when (IsUnauthorizedException(ex))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing admission for TicketID: {adm.TicketingTicketID}");
                    resultList.Add(new SaveResult { Success = false, Remarks = ex.Message });
                }
            }

            if (entitiesToCreate.Count > 0)
            {
                await CreateEntitiesInBatches(svc, entitiesToCreate, resultList);
            }

            return resultList;
        }

        private async Task<ValidationResult> ValidateTicketingAdmission(ticketingadmiss admission, ServiceClient svc)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(admission.TicketingTicketID))
            {
                result.IsValid = false;
                result.Remarks = "TicketingTicketID is required";
                return result;
            }

            if (string.IsNullOrWhiteSpace(admission.EventCode))
            {
                result.IsValid = false;
                result.Remarks = "EventCode is required";
                return result;
            }

            if (string.IsNullOrWhiteSpace(admission.MasterCustomerID) && string.IsNullOrWhiteSpace(admission.CustomerEmail))
            {
                result.IsValid = false;
                result.Remarks = "Either MasterCustomerID or CustomerEmail is required";
                return result;
            }

            var eventValidation = await ValidateEventCode(svc, admission.EventCode);
            if (!eventValidation.IsValid)
            {
                result.IsValid = false;
                result.Remarks = eventValidation.Remarks;
                return result;
            }

            var ticketValidation = await ValidateTicketIdNotExists(svc, admission.TicketingTicketID);
            if (!ticketValidation.IsValid)
            {
                result.IsValid = false;
                result.Remarks = ticketValidation.Remarks;
                return result;
            }

            if (!string.IsNullOrWhiteSpace(admission.AdmissionDateTime))
            {
                try
                {
                    var dateTime = DateTime.Parse(admission.AdmissionDateTime);
                    if (dateTime > DateTime.Now)
                    {
                        result.IsValid = false;
                        result.Remarks = "AdmissionDateTime cannot be in the future";
                        return result;
                    }
                }
                catch (FormatException)
                {
                    result.IsValid = false;
                    result.Remarks = "Invalid AdmissionDateTime format. Please use YYYY-MM-DD HH:MM:SS format (e.g., 2017-12-31 23:59:59)";
                    return result;
                }
            }

            var validationResult = await ValidateCustomerData(admission, svc);
            if (!validationResult.IsValid)
            {
                result.IsValid = false;
                result.Remarks = validationResult.Remarks;
                return result;
            }

            result.IsValid = true;
            return result;
        }

        private async Task<ValidationResult> ValidateEventCode(ServiceClient svc, string eventCode)
        {
            var result = new ValidationResult();

            try
            {
                var query = new QueryExpression("msevtmgt_event");
                query.ColumnSet.AddColumn("msevtmgt_eventid");
                query.Criteria.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventCode);
                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));

                if (results.Entities.Count == 0)
                {
                    result.IsValid = false;
                    result.Remarks = $"No event found with EventCode: {eventCode}";
                    return result;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Authentication failed when accessing Dataverse", ex);
                }

                result.IsValid = false;
                result.Remarks = $"Error validating event code: {ex.Message}";
                return result;
            }
        }

        private async Task<ValidationResult> ValidateTicketIdNotExists(ServiceClient svc, string ticketingTicketId)
        {
            var result = new ValidationResult();

            try
            {
                var query = new QueryExpression("wkcda_ticketingadmission");
                query.ColumnSet.AddColumn("wkcda_ticketingadmissionid");
                query.Criteria.AddCondition("wkcda_ticketingticketid", ConditionOperator.Equal, ticketingTicketId);
                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));

                if (results.Entities.Count > 0)
                {
                    result.IsValid = false;
                    result.Remarks = $"TicketingTicketID '{ticketingTicketId}' already exists in the record";
                    return result;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking duplicate ticket ID: {ticketingTicketId}");
                result.IsValid = false;
                result.Remarks = $"Error checking ticket ID: {ex.Message}";
                return result;
            }
        }

        private async Task<ValidationResult> ValidateCustomerData(ticketingadmiss admission, ServiceClient svc)
        {
            var result = new ValidationResult();

            bool hasCustomerId = !string.IsNullOrWhiteSpace(admission.MasterCustomerID);
            bool hasEmail = !string.IsNullOrWhiteSpace(admission.CustomerEmail);

            if (hasCustomerId && hasEmail)
            {
                var contact = await GetContactByMasterCustomerID(svc, admission.MasterCustomerID);
                if (contact == null)
                {
                    result.IsValid = false;
                    result.Remarks = $"No contact found with MasterCustomerID: {admission.MasterCustomerID}";
                    return result;
                }

                var contactEmail = contact.GetAttributeValue<string>("emailaddress1");
                if (!string.Equals(contactEmail, admission.CustomerEmail, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsValid = false;
                    result.Remarks = $"Email provided {admission.CustomerEmail} doesn't match email record for MasterCustomerID: {admission.MasterCustomerID}.";
                    return result;
                }

                result.IsValid = true;
                return result;
            }

            if (!hasCustomerId && hasEmail)
            {
                var contact = await GetContactByEmail(svc, admission.CustomerEmail);
                if (contact == null)
                {
                    result.IsValid = false;
                    result.Remarks = $"No contact found with Email: {admission.CustomerEmail}";
                    return result;
                }

                admission.MasterCustomerID = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                result.IsValid = true;
                return result;
            }

            if (hasCustomerId && !hasEmail)
            {
                var contact = await GetContactByMasterCustomerID(svc, admission.MasterCustomerID);
                if (contact == null)
                {
                    result.IsValid = false;
                    result.Remarks = $"No contact found with MasterCustomerID: {admission.MasterCustomerID}";
                    return result;
                }

                admission.CustomerEmail = contact.GetAttributeValue<string>("emailaddress1");
                result.IsValid = true;
                return result;
            }

            result.IsValid = false;
            result.Remarks = "Both MasterCustomerID and CustomerEmail are missing";
            return result;
        }

        private async Task<Entity> GetContactByMasterCustomerID(ServiceClient svc, string masterCustomerID)
        {
            try
            {
                var query = new QueryExpression("contact");
                query.ColumnSet.AddColumns("contactid", "emailaddress1", "wkcda_mastercustomerid");
                query.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerID);
                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));
                return results.Entities.Count > 0 ? results.Entities[0] : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error looking up contact with MasterCustomerID: {masterCustomerID}");
                return null;
            }
        }

        private async Task<Entity> GetContactByEmail(ServiceClient svc, string email)
        {
            try
            {
                var query = new QueryExpression("contact");
                query.ColumnSet.AddColumns("contactid", "emailaddress1", "wkcda_mastercustomerid");
                query.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));
                return results.Entities.Count > 0 ? results.Entities[0] : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error looking up contact with Email: {email}");
                return null;
            }
        }

        private async Task<Entity> CreateTicketingAdmissionEntity(ticketingadmiss admission, ServiceClient svc)
        {
            var entity = new Entity("wkcda_ticketingadmission");

            entity["wkcda_ticketingticketid"] = admission.TicketingTicketID;
            entity["wkcda_mastercustomerid"] = admission.MasterCustomerID;
            entity["wkcda_customeremail"] = admission.CustomerEmail;
            entity["wkcda_event"] = getEventLookupRef(svc, admission.EventCode);

            var contactRef = await getContactLookupRef(svc, admission.MasterCustomerID);
            if (contactRef != null)
            {
                entity["wkcda_member"] = contactRef;
            }

            if (admission.EntranceAdmitted.HasValue)
            {
                entity["wkcda_entranceadmitted"] = admission.EntranceAdmitted.Value ? "True" : "False";
            }

            if (!string.IsNullOrWhiteSpace(admission.AdmissionDateTime))
            {
                try
                {
                    DateTime parsedDateTime = DateTime.ParseExact(
                        admission.AdmissionDateTime,
                        "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture
                    );

                    entity["wkcda_admissiondatetime"] = parsedDateTime;
                    _logger.LogInformation($"Successfully parsed datetime: {admission.AdmissionDateTime} -> {parsedDateTime}");
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning($"Invalid datetime format for TicketID: {admission.TicketingTicketID}. Input: '{admission.AdmissionDateTime}'. Error: {ex.Message}. Setting to null.");
                    entity["wkcda_admissiondatetime"] = null;
                }
            }

            return entity;
        }

        private async Task CreateEntitiesInBatches(ServiceClient svc, List<Entity> entities, List<SaveResult> resultList)
        {
            try
            {
                foreach (var entity in entities)
                {
                    try
                    {
                        await svc.CreateAsync(entity);
                        _logger.LogInformation($"Successfully created ticketing admission for TicketID: {entity.GetAttributeValue<string>("wkcda_ticketingticketid")}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to create ticketing admission for TicketID: {entity.GetAttributeValue<string>("wkcda_ticketingticketid")}");
                        var ticketId = entity.GetAttributeValue<string>("wkcda_ticketingticketid");
                        for (int i = 0; i < resultList.Count; i++)
                        {
                            if (resultList[i].Success == true && resultList[i].Remarks == null)
                            {
                                resultList[i] = new SaveResult { Success = false, Remarks = $"Failed to create record: {ex.Message}" };
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during batch entity creation");
                throw;
            }
        }

        private EntityReference getEventLookupRef(ServiceClient svc, string eventCode)
        {
            if (string.IsNullOrWhiteSpace(eventCode))
                return null;

            try
            {
                var query = new QueryExpression("msevtmgt_event");
                query.ColumnSet.AddColumn("msevtmgt_eventid");
                query.Criteria.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventCode);
                query.TopCount = 1;

                var results = svc.RetrieveMultiple(query);

                if (results.Entities.Count > 0)
                {
                    var eventEntity = results.Entities[0];
                    return eventEntity.ToEntityReference();
                }
                else
                {
                    _logger.LogWarning($"No event found with EventCode: {eventCode}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error looking up event with EventCode: {eventCode}");
                return null;
            }
        }

        private async Task<EntityReference> getContactLookupRef(ServiceClient svc, string masterCustomerID)
        {
            if (string.IsNullOrWhiteSpace(masterCustomerID))
                return null;

            try
            {
                var query = new QueryExpression("contact");
                query.ColumnSet.AddColumn("contactid");

                query.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerID);

                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));

                if (results.Entities.Count > 0)
                {
                    var contactEntity = results.Entities[0];
                    _logger.LogInformation($"Found contact with ID: {contactEntity.Id} for MasterCustomerID: {masterCustomerID}");
                    return contactEntity.ToEntityReference();
                }
                else
                {
                    _logger.LogWarning($"No contact found with MasterCustomerID: {masterCustomerID}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error looking up contact with MasterCustomerID: {masterCustomerID}");
                return null;
            }
        }

        private async Task<bool> CheckContactExists(ServiceClient svc, string masterCustomerID)
        {
            if (string.IsNullOrWhiteSpace(masterCustomerID))
                return false;

            try
            {
                var query = new QueryExpression("contact");
                query.ColumnSet.AddColumn("contactid");
                query.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerID);
                query.TopCount = 1;

                var results = await Task.Run(() => svc.RetrieveMultiple(query));
                return results.Entities.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking contact existence for MasterCustomerID: {masterCustomerID}");
                return false;
            }
        }

        #endregion

        #region Helper Classes

        private class ValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public List<ticketingadmiss> ticketingadmiss { get; set; }
        }

        public class ticketingadmiss
        {
            public string TicketingTicketID { get; set; }
            public string MasterCustomerID { get; set; }
            public string CustomerEmail { get; set; }
            public string EventCode { get; set; }
            public string AdmissionDateTime { get; set; }
            public bool? EntranceAdmitted { get; set; }
        }

        public class SaveResult
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
        }

        #endregion
    }
}