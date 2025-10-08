using Microsoft.AspNetCore.Http;
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
using DataverseModel;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class RetrieveEventTransactionWS : WS_Base
    {
        public RetrieveEventTransactionWS(ILogger<RetrieveEventTransactionWS> logger) : base(logger) { }

        [Function("RetrieveEventTransactionWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveEventTransactionWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrieveEventTransactionWS");

            ServiceClient svc = null;
            try
            {
                svc = GetServiceClient(req);

                await TestAuthentication(svc);

                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.EventKey == null)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var eventKey = requestBody.EventKey;
                var response = new ResponseBody();

                if (string.IsNullOrWhiteSpace(eventKey.EventCode) && string.IsNullOrWhiteSpace(eventKey.PersonAccount))
                {
                    response.Success = false;
                    response.Remarks = "Please provide EventCode & PersonAccount.";
                    return new OkObjectResult(response);
                }

                if (string.IsNullOrWhiteSpace(eventKey.EventCode))
                {
                    response.Success = false;
                    response.Remarks = "EventCode is required.";
                    return new OkObjectResult(response);
                }

                if (string.IsNullOrWhiteSpace(eventKey.PersonAccount))
                {
                    response.Success = false;
                    response.Remarks = "PersonAccount is required.";
                    return new OkObjectResult(response);
                }

                var contactId = GetContactIdByPersonAccount(svc, eventKey.PersonAccount);
                if (contactId == Guid.Empty)
                {
                    response.Success = false;
                    response.Remarks = $"PersonAccount '{eventKey.PersonAccount}' not found.";
                    _logger.LogInformation($"Account not found. PersonAccount: {eventKey.PersonAccount}");
                    return new OkObjectResult(response);
                }

                var eventExists = CheckEventCodeExists(svc, eventKey.EventCode);
                if (!eventExists)
                {
                    response.Success = false;
                    response.Remarks = $"Event with EventCode '{eventKey.EventCode}' not found.";
                    _logger.LogInformation($"Event not found. EventCode: {eventKey.EventCode}");
                    return new OkObjectResult(response);
                }

                var transactionList = GetEventTransactions(svc, eventKey, contactId);

                response.Success = true;
                response.TransactionList = transactionList;

                if (transactionList.Count == 0)
                {
                    response.Remarks = "No transaction records exist for the specified account and event.";
                    response.Success = false;
                    _logger.LogInformation($"No transactions found. EventCode: {eventKey.EventCode}, PersonAccount: {eventKey.PersonAccount}");
                }
                else
                {
                    response.EventCode = eventKey.EventCode;
                    response.PersonAccount = eventKey.PersonAccount;
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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
                _logger.LogError(ex, "Error retrieving event transactions");
                return new ObjectResult(new { error = "Bad Request", message = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
            finally
            {
                svc?.Dispose();
            }
        }

        private async Task TestAuthentication(ServiceClient svc)
        {
            try
            {
                var testQuery = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid"),
                    TopCount = 1
                };

                var result = await Task.Run(() => svc.RetrieveMultiple(testQuery));
                _logger.LogInformation("Authentication test successful");
            }
            catch (Exception ex)
            {
                if (IsUnauthorizedException(ex))
                {
                    throw new Exception("Unauthorized: Authentication failed", ex);
                }
                _logger.LogWarning($"Non-authentication error during auth test: {ex.Message}");
            }
        }

        private List<Transaction> GetEventTransactions(ServiceClient svc, EventKey eventKey, Guid contactId)
        {
            var transactions = new List<Transaction>();

            try
            {
                var query = new QueryExpression("wkcda_eventtransaction")
                {
                    ColumnSet = new ColumnSet(
                        "wkcda_titleonbadge", "wkcda_status", "wkcda_seatnosection", "wkcda_seatnorow",
                        "wkcda_seatnoseat", "wkcda_salutation", "wkcda_registrationsource", "wkcda_registrationdate",
                        "wkcda_registrantlastnameeng", "wkcda_registrantfirstnameeng", "wkcda_purchasingmethod",
                        "wkcda_programmevenue", "wkcda_pricezoneclasstier", "wkcda_price", "wkcda_personalemail",
                        "wkcda_paymentmethod", "wkcda_numberoftickets", "wkcda_numberofparticipants", "wkcda_nameonbadge",
                        "wkcda_mobilephoneno", "wkcda_eventname", "wkcda_discountcode", "wkcda_accountnameeng",
                        "wkcda_answerremark1", "wkcda_answerremark2", "wkcda_answerremark3", "wkcda_answerremark4",
                        "wkcda_answerremark5", "wkcda_answerremark6", "wkcda_answerremark7", "wkcda_answerremark8",
                        "wkcda_answerremark9", "wkcda_answerremark10", "wkcda_answerremark11", "wkcda_answerremark12",
                        "wkcda_answerremark13", "wkcda_answerremark14", "wkcda_answerremark15", "wkcda_answerremark16",
                        "wkcda_answerremark17", "wkcda_answerremark18", "wkcda_answerremark19", "wkcda_answerremark20",
                         "wkcda_membername", "wkcda_eventrelated"
                    ),
                    PageInfo = new PagingInfo
                    {
                        PageNumber = eventKey.PageNum,
                        Count = 50
                    }
                };

                var eventLink = query.AddLink("msevtmgt_event", "wkcda_eventrelated", "msevtmgt_eventid");
                eventLink.Columns = new ColumnSet("msevtmgt_eventid", "msevtmgt_name", "wkcda_eventcode");
                eventLink.EntityAlias = "event";

                if (!string.IsNullOrWhiteSpace(eventKey.EventCode))
                {
                    eventLink.LinkCriteria.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventKey.EventCode);
                }

                query.Criteria.AddCondition("wkcda_membername", ConditionOperator.Equal, contactId);

                _logger.LogInformation($"Executing query with EventCode: {eventKey.EventCode}, PersonAccount: {eventKey.PersonAccount}, PageNum: {eventKey.PageNum}");

                var result = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Query returned {result.Entities.Count} records");

                foreach (var entity in result.Entities)
                {
                    var transaction = new Transaction
                    {
                        Id = entity.Id.ToString(),
                        TitleOnBadge = entity.GetAttributeValue<string>("wkcda_titleonbadge"),

                        Status = GetOptionSetText(entity, "wkcda_status"),
                        SeatSection = GetOptionSetText(entity, "wkcda_seatnosection"),
                        RegistrationSource = GetOptionSetText(entity, "wkcda_registrationsource"),
                        PurchasingMethod = GetOptionSetText(entity, "wkcda_purchasingmethod"),
                        PriceZone = GetOptionSetText(entity, "wkcda_pricezoneclasstier"),
                        PaymentMethod = GetOptionSetText(entity, "wkcda_paymentmethod"),

                        SeatRow = entity.GetAttributeValue<string>("wkcda_seatnorow"),
                        SeatNo = entity.GetAttributeValue<string>("wkcda_seatnoseat"),
                        Salutation = entity.GetAttributeValue<string>("wkcda_salutation"),

                        RegistrationDate = entity.GetAttributeValue<DateTime?>("wkcda_registrationdate")?.ToString("yyyy-MM-dd"),
                        RegistrantLastName = entity.GetAttributeValue<string>("wkcda_registrantlastnameeng"),
                        RegistrantFirstName = entity.GetAttributeValue<string>("wkcda_registrantfirstnameeng"),
                        ProgrammeVenue = entity.GetAttributeValue<string>("wkcda_programmevenue"),

                        Price = entity.GetAttributeValue<Money>("wkcda_price")?.Value,

                        PersonEmail = entity.GetAttributeValue<string>("wkcda_personalemail"),

                        NumofTickets = GetFloatAsInt(entity, "wkcda_numberoftickets"),
                        NoofParticipants = GetFloatAsInt(entity, "wkcda_numberofparticipants"),

                        NameOnBadge = entity.GetAttributeValue<string>("wkcda_nameonbadge"),
                        Mobile = entity.GetAttributeValue<string>("wkcda_mobilephoneno"),
                        Event = entity.GetAttributeValue<string>("wkcda_eventname"),
                        DiscountCode = entity.GetAttributeValue<string>("wkcda_discountcode"),
                        AccountName = entity.GetAttributeValue<string>("wkcda_accountnameeng"),

                        Remark1 = entity.GetAttributeValue<string>("wkcda_answerremark1"),
                        Remark2 = entity.GetAttributeValue<string>("wkcda_answerremark2"),
                        Remark3 = entity.GetAttributeValue<string>("wkcda_answerremark3"),
                        Remark4 = entity.GetAttributeValue<string>("wkcda_answerremark4"),
                        Remark5 = entity.GetAttributeValue<string>("wkcda_answerremark5"),
                        Remark6 = entity.GetAttributeValue<string>("wkcda_answerremark6"),
                        Remark7 = entity.GetAttributeValue<string>("wkcda_answerremark7"),
                        Remark8 = entity.GetAttributeValue<string>("wkcda_answerremark8"),
                        Remark9 = entity.GetAttributeValue<string>("wkcda_answerremark9"),
                        Remark10 = entity.GetAttributeValue<string>("wkcda_answerremark10"),
                        Remark11 = entity.GetAttributeValue<string>("wkcda_answerremark11"),
                        Remark12 = entity.GetAttributeValue<string>("wkcda_answerremark12"),
                        Remark13 = entity.GetAttributeValue<string>("wkcda_answerremark13"),
                        Remark14 = entity.GetAttributeValue<string>("wkcda_answerremark14"),
                        Remark15 = entity.GetAttributeValue<string>("wkcda_answerremark15"),
                        Remark16 = entity.GetAttributeValue<string>("wkcda_answerremark16"),
                        Remark17 = entity.GetAttributeValue<string>("wkcda_answerremark17"),
                        Remark18 = entity.GetAttributeValue<string>("wkcda_answerremark18"),
                        Remark19 = entity.GetAttributeValue<string>("wkcda_answerremark19"),
                        Remark20 = entity.GetAttributeValue<string>("wkcda_answerremark20"),
                    };

                    var memberRef = entity.GetAttributeValue<EntityReference>("wkcda_membername");
                    if (memberRef != null)
                    {
                        transaction.PersonAccount = GetPersonAccountFromContact(svc, memberRef.Id);
                    }

                    transactions.Add(transaction);
                }
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed in GetEventTransactions");
                throw new Exception($"Unauthorized: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetEventTransactions");
                throw;
            }

            return transactions;
        }

        private Guid GetContactIdByPersonAccount(ServiceClient svc, string personAccount)
        {
            try
            {
                var contactQuery = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_mastercustomerid", ConditionOperator.Equal, personAccount)
                        }
                    }
                };

                var contactResult = svc.RetrieveMultiple(contactQuery);
                if (contactResult.Entities.Count > 0)
                {
                    return contactResult.Entities[0].Id;
                }

                _logger.LogWarning($"No contact found with master customer ID: {personAccount}");
                return Guid.Empty;
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed in GetContactIdByPersonAccount");
                throw new Exception($"Unauthorized: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving contact by PersonAccount: {personAccount}");
                return Guid.Empty;
            }
        }

        private bool CheckEventCodeExists(ServiceClient svc, string eventCode)
        {
            try
            {
                var eventQuery = new QueryExpression("msevtmgt_event")
                {
                    ColumnSet = new ColumnSet("msevtmgt_eventid"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_eventcode", ConditionOperator.Equal, eventCode)
                        }
                    }
                };

                var eventResult = svc.RetrieveMultiple(eventQuery);
                return eventResult.Entities.Count > 0;
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed in CheckEventCodeExists");
                throw new Exception($"Unauthorized: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking event code existence: {eventCode}");
                return false;
            }
        }

        private string GetPersonAccountFromContact(ServiceClient svc, Guid contactId)
        {
            try
            {
                var contact = svc.Retrieve("contact", contactId, new ColumnSet("wkcda_mastercustomerid"));
                return contact.GetAttributeValue<string>("wkcda_mastercustomerid");
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed in GetPersonAccountFromContact");
                throw new Exception($"Unauthorized: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving PersonAccount for contact {contactId}");
                return null;
            }
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

        private int? GetFloatAsInt(Entity entity, string attributeName)
        {
            try
            {
                var decimalValue = entity.GetAttributeValue<decimal?>(attributeName);
                if (decimalValue.HasValue)
                {
                    return (int)decimalValue.Value;
                }

                var doubleValue = entity.GetAttributeValue<double?>(attributeName);
                if (doubleValue.HasValue)
                {
                    return (int)doubleValue.Value;
                }

                return entity.GetAttributeValue<int?>(attributeName);
            }
            catch
            {
                return null;
            }
        }

        private bool IsUnauthorizedException(Exception ex)
        {
            if (ex == null) return false;

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

        #region Request/Response Classes

        public class RequestBody
        {
            public EventKey EventKey { get; set; }
        }

        public class EventKey
        {
            public string EventCode { get; set; }
            public string PersonAccount { get; set; }
            public int PageNum { get; set; } = 1;
        }

        public class ResponseBody
        {
            public List<Transaction> TransactionList { get; set; }
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string PersonAccount { get; set; }
            public string EventCode { get; set; }
            public string SessionCode { get; set; }
        }

        public class Transaction
        {
            public string TitleOnBadge { get; set; }
            public string Status { get; set; }
            public string SeatSection { get; set; }
            public string SeatRow { get; set; }
            public string SeatNo { get; set; }
            public string Salutation { get; set; }
            public string RegistrationSource { get; set; }
            public string RegistrationDate { get; set; }
            public string RegistrantLastName { get; set; }
            public string RegistrantFirstName { get; set; }
            public string PurchasingMethod { get; set; }
            public string ProgrammeVenue { get; set; }
            public string PriceZone { get; set; }
            public decimal? Price { get; set; }
            public string PersonEmail { get; set; }
            public string PersonAccount { get; set; }
            public string PaymentMethod { get; set; }
            public int? NumofTickets { get; set; }
            public int? NoofParticipants { get; set; }
            public string NameOnBadge { get; set; }
            public string Mobile { get; set; }
            public string Id { get; set; }
            public string Event { get; set; }
            public string DiscountCode { get; set; }
            public string AccountName { get; set; }
            public string FormId { get; set; }

            public string Remark1 { get; set; }
            public string Remark2 { get; set; }
            public string Remark3 { get; set; }
            public string Remark4 { get; set; }
            public string Remark5 { get; set; }
            public string Remark6 { get; set; }
            public string Remark7 { get; set; }
            public string Remark8 { get; set; }
            public string Remark9 { get; set; }
            public string Remark10 { get; set; }
            public string Remark11 { get; set; }
            public string Remark12 { get; set; }
            public string Remark13 { get; set; }
            public string Remark14 { get; set; }
            public string Remark15 { get; set; }
            public string Remark16 { get; set; }
            public string Remark17 { get; set; }
            public string Remark18 { get; set; }
            public string Remark19 { get; set; }
            public string Remark20 { get; set; }
        }

        #endregion
    }
}