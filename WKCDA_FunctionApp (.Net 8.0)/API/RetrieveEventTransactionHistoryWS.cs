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
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class RetrieveEventTransactionHistoryWS : WS_Base
    {
        public RetrieveEventTransactionHistoryWS(ILogger<RetrieveEventTransactionHistoryWS> logger) : base(logger) { }

        [Function("RetrieveEventTransactionHistoryWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveEventTransactionHistoryWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrieveEventTransactionHistoryWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.RetrieveInfo == null)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var retrieveInfo = requestBody.RetrieveInfo;
                var response = new ResponseBody();

                if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    response.Success = false;
                    response.Remarks = "Please provide CustomerID or Email.";
                    //     response.RetrieveInfo = retrieveInfo;
                    //return new OkObjectResult(response);
                    return new ObjectResult(new { error = response })
                    {
                        StatusCode = StatusCodes.Status400BadRequest
                    };
                }

                var dateValidationResult = ValidateDates(retrieveInfo.StartDate, retrieveInfo.EndDate);
                if (!dateValidationResult.IsValid)
                {
                    response.Success = false;
                    response.Remarks = dateValidationResult.Remarks;
                    response.RetrieveInfo = retrieveInfo;
                    return new OkObjectResult(response);
                }

                var contactValidationResult = ValidateAndGetContact(svc, retrieveInfo);
                if (!contactValidationResult.IsValid)
                {
                    response.Success = false;
                    response.Remarks = contactValidationResult.Remarks;
                    //  response.RetrieveInfo = retrieveInfo;
                    //  return new OkObjectResult(response);
                    return new ObjectResult(new { error = response })
                    {
                        StatusCode = StatusCodes.Status400BadRequest
                    };
                }

                int offsetNum = GetOffsetNum(retrieveInfo.PageNum);
                if (offsetNum > 2000)
                {
                    response.Success = false;
                    response.Remarks = "Beyond MAX Data rows, please change 'Start Date' and 'End Date'.";
                    response.RetrieveInfo = retrieveInfo;
                    return new OkObjectResult(response);
                }

                var transactionList = GetEventTransactionHistory(svc, retrieveInfo, offsetNum, contactValidationResult.ContactRef, contactValidationResult.ContactMasterId, dateValidationResult.StartDate, dateValidationResult.EndDate);

                response.Success = true;
                response.RetrieveInfo = retrieveInfo;
                response.TransactionList = transactionList;

                if (transactionList.Count == 0)
                {
                    response.Remarks = "No transactions found for the given criteria";
                    _logger.LogInformation($"No transactions found. CustomerID: {retrieveInfo.CustomerID}, Email: {retrieveInfo.Email}, StartDate: {retrieveInfo.StartDate}, EndDate: {retrieveInfo.EndDate}");
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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

        private DateValidationResult ValidateDates(string startDateStr, string endDateStr)
        {
            var result = new DateValidationResult();

            if (string.IsNullOrWhiteSpace(startDateStr) && string.IsNullOrWhiteSpace(endDateStr))
            {
                result.IsValid = true;
                return result;
            }

            if (string.IsNullOrWhiteSpace(startDateStr) != string.IsNullOrWhiteSpace(endDateStr))
            {
                result.IsValid = false;
                result.Remarks = "Both Start Date and End Date must be provided together, or leave both empty.";
                return result;
            }

            // Validate date formats
            try
            {
                result.StartDate = DateTime.Parse(startDateStr);
                result.EndDate = DateTime.Parse(endDateStr);

                if (result.EndDate < result.StartDate)
                {
                    result.IsValid = false;
                    result.Remarks = "End Date cannot be earlier than Start Date.";
                    return result;
                }

                if (result.StartDate > DateTime.Today || result.EndDate > DateTime.Today)
                {
                    result.IsValid = false;
                    result.Remarks = "Date cannot be in the future.";
                    return result;
                }

                result.IsValid = true;
            }
            catch (FormatException)
            {
                result.IsValid = false;
                result.Remarks = "Invalid date format. Please use YYYY-MM-DD format (e.g., 2020-12-31).";
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Remarks = "Error processing dates. Please check the date formats.";
                _logger.LogError(ex, "Error validating dates");
            }

            return result;
        }

        private ContactValidationResult ValidateAndGetContact(ServiceClient svc, RetrieveInfo retrieveInfo)
        {
            var result = new ContactValidationResult();

            try
            {
                // Check if both fields are empty
                if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    result.IsValid = false;
                    result.Remarks = "Please provide either CustomerID or Email.";
                    _logger.LogWarning(result.Remarks);
                    return result;
                }

                EntityReference contactRef = null;
                string contactMasterId = null;
                Entity contact = null;

                // Scenario 1: Both CustomerID and Email provided
                if (!string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && !string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    _logger.LogInformation($"Looking up contact by CustomerID: {retrieveInfo.CustomerID}");

                    var contactQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "fullname", "emailaddress1"),
                        Criteria = new FilterExpression
                        {
                            Conditions =
                    {
                        new ConditionExpression("wkcda_mastercustomerid", ConditionOperator.Equal, retrieveInfo.CustomerID)
                    }
                        }
                    };

                    var contactResult = svc.RetrieveMultiple(contactQuery);
                    if (contactResult.Entities.Count > 0)
                    {
                        contact = contactResult.Entities[0];
                        contactRef = contact.ToEntityReference();
                        contactMasterId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var contactName = contact.GetAttributeValue<string>("fullname");
                        var contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        // Validate email matches
                        if (!string.Equals(contactEmail, retrieveInfo.Email, StringComparison.OrdinalIgnoreCase))
                        {
                            result.IsValid = false;
                            result.Remarks = $"Email mismatch. Contact email does not match provided email '{retrieveInfo.Email}' for Customer: {retrieveInfo.CustomerID}";
                            _logger.LogWarning(result.Remarks);
                            return result;
                        }
                        else
                        {
                            _logger.LogInformation($"Email validation successful for MasterCustomerID: {retrieveInfo.CustomerID}");
                        }

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with CustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
                // Scenario 2: Only CustomerID provided
                else if (!string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    _logger.LogInformation($"Looking up contact by CustomerID only: {retrieveInfo.CustomerID}");

                    var contactQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "fullname", "emailaddress1"),
                        Criteria = new FilterExpression
                        {
                            Conditions =
                    {
                        new ConditionExpression("wkcda_mastercustomerid", ConditionOperator.Equal, retrieveInfo.CustomerID)
                    }
                        }
                    };

                    var contactResult = svc.RetrieveMultiple(contactQuery);
                    if (contactResult.Entities.Count > 0)
                    {
                        contact = contactResult.Entities[0];
                        contactRef = contact.ToEntityReference();
                        contactMasterId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var contactName = contact.GetAttributeValue<string>("fullname");
                        var contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with CustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
                // Scenario 3: Only Email provided
                else if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && !string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    _logger.LogInformation($"Looking up contact by Email only: {retrieveInfo.Email}");

                    var contactQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "fullname", "emailaddress1"),
                        Criteria = new FilterExpression
                        {
                            Conditions =
                    {
                        new ConditionExpression("emailaddress1", ConditionOperator.Equal, retrieveInfo.Email)
                    }
                        }
                    };

                    var contactResult = svc.RetrieveMultiple(contactQuery);
                    if (contactResult.Entities.Count > 0)
                    {
                        if (contactResult.Entities.Count > 1)
                        {
                            _logger.LogWarning($"Multiple contacts found with email: {retrieveInfo.Email}. Using the first one.");
                        }

                        contact = contactResult.Entities[0];
                        contactRef = contact.ToEntityReference();
                        contactMasterId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var contactName = contact.GetAttributeValue<string>("fullname");
                        var contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with Email: {retrieveInfo.Email}";
                        _logger.LogWarning(result.Remarks);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }

                result.IsValid = false;
                result.Remarks = $"Error validating contact: {ex.Message}";
                _logger.LogError(ex, "Error in ValidateAndGetContact");
                return result;
            }
        }

        private int GetOffsetNum(int pageNum)
        {
            int pageSize = 50;
            return (pageNum - 1) * pageSize;
        }

        private List<Transaction> GetEventTransactionHistory(ServiceClient svc, RetrieveInfo retrieveInfo, int offsetNum, EntityReference contactRef, string contactMasterId, DateTime? startDate = null, DateTime? endDate = null)
        {
            var transactions = new List<Transaction>();

            try
            {
                var testQuery = new QueryExpression("wkcda_eventtransaction")
                {
                    ColumnSet = new ColumnSet("wkcda_eventtransactionid"),
                };

                var testResult = svc.RetrieveMultiple(testQuery);
                _logger.LogInformation($"Total event transactions in system: {testResult.Entities.Count}");

                if (testResult.Entities.Count == 0)
                {
                    _logger.LogWarning("No event transactions exist in the system at all!");
                    return transactions;
                }

                var relationshipTestQuery = new QueryExpression("wkcda_eventtransaction")
                {
                    ColumnSet = new ColumnSet("wkcda_eventtransactionid", "wkcda_registrationdate", "wkcda_newcolumn"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_membername", ConditionOperator.Equal, contactRef.Id)
                        }
                    },
                };

                var relationshipTestResult = svc.RetrieveMultiple(relationshipTestQuery);
                _logger.LogInformation($"Event transactions found using wkcda_membername relationship: {relationshipTestResult.Entities.Count}");

                foreach (var tx in relationshipTestResult.Entities)
                {
                    var txDate = tx.GetAttributeValue<DateTime?>("wkcda_registrationdate");
                    var eventName = tx.GetAttributeValue<string>("wkcda_eventname");
                    _logger.LogInformation($"Transaction found: ID={tx.Id}, Date={txDate}, Event={eventName}");
                }

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
                        "wkcda_answerremark11", "wkcda_answerremark12", "wkcda_answerremark13", "wkcda_answerremark14",
                        "wkcda_answerremark15", "wkcda_answerremark16", "wkcda_answerremark17", "wkcda_answerremark18",
                        "wkcda_answerremark19", "wkcda_answerremark20",
                        "wkcda_answerremark9", "wkcda_answerremark10", "wkcda_membername"
                    ),
                    PageInfo = new PagingInfo
                    {
                        PageNumber = retrieveInfo.PageNum,
                        Count = 50
                    }
                };

                query.Criteria.AddCondition("wkcda_membername", ConditionOperator.Equal, contactRef.Id);

                // Add date range filter
                if (startDate.HasValue && endDate.HasValue)
                {
                    var adjustedEndDate = endDate.Value.AddDays(1).AddSeconds(-1);

                    query.Criteria.AddCondition("wkcda_registrationdate", ConditionOperator.OnOrAfter, startDate.Value);
                    query.Criteria.AddCondition("wkcda_registrationdate", ConditionOperator.OnOrBefore, adjustedEndDate);

                    _logger.LogInformation($"Date filter applied: {startDate.Value:yyyy-MM-dd} to {adjustedEndDate:yyyy-MM-dd}");
                }


                _logger.LogInformation($"Executing final query with contact ID: {contactRef.Id} using wkcda_membername relationship");

                // Execute query
                var result = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Final query returned {result.Entities.Count} records");

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

                        PersonAccount = contactMasterId
                    };

                    transactions.Add(transaction);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetEventTransactionHistory");
            }

            return transactions;
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

        #endregion

        #region Helper Classes

        private class ContactValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
            public EntityReference ContactRef { get; set; }
            public string ContactMasterId { get; set; }
        }

        private class DateValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public RetrieveInfo RetrieveInfo { get; set; }
        }

        public class RetrieveInfo
        {
            public string StartDate { get; set; }
            public int PageNum { get; set; }
            public string EndDate { get; set; }
            public string Email { get; set; }
            public string CustomerID { get; set; }
        }

        public class ResponseBody
        {
            public List<Transaction> TransactionList { get; set; }
            public bool Success { get; set; }
            public RetrieveInfo RetrieveInfo { get; set; }
            public string Remarks { get; set; }
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