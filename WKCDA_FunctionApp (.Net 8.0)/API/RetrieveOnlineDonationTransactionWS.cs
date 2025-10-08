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
    public class RetrieveOnlineDonationTransactionWS : WS_Base
    {
        public RetrieveOnlineDonationTransactionWS(ILogger<RetrieveOnlineDonationTransactionWS> logger) : base(logger) { }

        [Function("RetrieveOnlineDonationTransactionWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "services/apexrest/WKCDA/RetrieveOnlineDonationTransactionWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrieveOnlineDonationTransactionWS");

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

                var donationTransList = GetOnlineDonationTransactionHistory(svc, retrieveInfo, offsetNum, contactValidationResult.ContactRef, contactValidationResult.ContactMasterId, contactValidationResult.ContactEmail, dateValidationResult.StartDate, dateValidationResult.EndDate);

                response.Success = true;
                response.RetrieveInfo = retrieveInfo;
                response.DonationTransList = donationTransList;

                if (donationTransList.Count == 0)
                {
                    response.Remarks = "No donation transactions found for the given criteria";
                    _logger.LogInformation($"No donation transactions found. CustomerID: {retrieveInfo.CustomerID}, Email: {retrieveInfo.Email}, StartDate: {retrieveInfo.StartDate}, EndDate: {retrieveInfo.EndDate}");
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
                result.Remarks = "For filtering logic StartDate and EndDate should be provided in pair";
                return result;
            }

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
                if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    result.IsValid = false;
                    result.Remarks = "Please provide either CustomerID or Email.";
                    _logger.LogWarning(result.Remarks);
                    return result;
                }

                EntityReference contactRef = null;
                string contactMasterId = null;
                string contactEmail = null;
                Entity contact = null;

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
                        contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

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
                        result.ContactEmail = contactEmail;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with CustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
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
                        contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                        result.ContactEmail = contactEmail;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with CustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
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
                        contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                        result.ContactEmail = contactEmail;
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

        private List<DonationTransaction> GetOnlineDonationTransactionHistory(ServiceClient svc, RetrieveInfo retrieveInfo, int offsetNum, EntityReference contactRef, string contactMasterId, string contactEmail, DateTime? startDate = null, DateTime? endDate = null)
        {
            var donationTransactions = new List<DonationTransaction>();

            try
            {
                var testQuery = new QueryExpression("wkcda_gifttransaction")
                {
                    ColumnSet = new ColumnSet("wkcda_gifttransactionid"),
                };

                var testResult = svc.RetrieveMultiple(testQuery);
                _logger.LogInformation($"Total online donation transactions in system: {testResult.Entities.Count}");

                if (testResult.Entities.Count == 0)
                {
                    _logger.LogWarning("No online donation transactions exist in the system at all!");
                    return donationTransactions;
                }

                var relationshipTestQuery = new QueryExpression("wkcda_gifttransaction")
                {
                    ColumnSet = new ColumnSet("wkcda_gifttransactionid", "wkcda_giftdate", "wkcda_giftid"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactRef.Id)
                        }
                    },
                };

                var relationshipTestResult = svc.RetrieveMultiple(relationshipTestQuery);
                _logger.LogInformation($"Online donation transactions found using wkcda_member relationship: {relationshipTestResult.Entities.Count}");

                foreach (var tx in relationshipTestResult.Entities)
                {
                    var txDate = tx.GetAttributeValue<DateTime?>("wkcda_giftdate");
                    var giftId = tx.GetAttributeValue<string>("wkcda_giftid");
                    _logger.LogInformation($"Donation transaction found: ID={tx.Id}, Date={txDate}, GiftID={giftId}");
                }

                var query = new QueryExpression("wkcda_gifttransaction")
                {
                    ColumnSet = new ColumnSet(
                        "wkcda_primaryaddresscountry",
                        "wkcda_transactionnumber",
                        "wkcda_banktransferreferencenumber",
                        "wkcda_primarycontact",
                        "wkcda_firstname",
                        "wkcda_memberemail",
                        "wkcda_paymenttransaction",
                        "wkcda_remarks",
                        "wkcda_giftdate",
                        "wkcda_giftid",
                        "wkcda_gifttype",
                        "wkcda_giftamount",
                        "wkcda_member"
                    ),
                    PageInfo = new PagingInfo
                    {
                        PageNumber = retrieveInfo.PageNum,
                        Count = 50
                    }
                };

                query.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactRef.Id);

                if (startDate.HasValue && endDate.HasValue)
                {
                    var adjustedEndDate = endDate.Value.AddDays(1).AddSeconds(-1);

                    query.Criteria.AddCondition("wkcda_giftdate", ConditionOperator.OnOrAfter, startDate.Value);
                    query.Criteria.AddCondition("wkcda_giftdate", ConditionOperator.OnOrBefore, adjustedEndDate);

                    _logger.LogInformation($"Date filter applied: {startDate.Value:yyyy-MM-dd} to {adjustedEndDate:yyyy-MM-dd}");
                }

                _logger.LogInformation($"Executing final query with contact ID: {contactRef.Id} using wkcda_member relationship");

                // Execute query
                var result = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Final query returned {result.Entities.Count} records");

                foreach (var entity in result.Entities)
                {
                    decimal? donationAmount = null;
                    var moneyValue = entity.GetAttributeValue<Money>("wkcda_giftamount");
                    if (moneyValue != null)
                    {
                        donationAmount = moneyValue.Value;
                    }

                    var usCitizenValue = GetLookupValue(entity, "wkcda_primaryaddresscountry");
                    var donationTransaction = new DonationTransaction
                    {
                        //USCitizen = GetLookupValue(entity, "wkcda_primaryaddresscountry"),
                        USCitizen = yesnoUSCitizen(usCitizenValue),
                        TransactionNo = entity.GetAttributeValue<string>("wkcda_transactionnumber"),
                        ReferenceNo = entity.GetAttributeValue<string>("wkcda_banktransferreferencenumber"),
                        PrimaryContactPhone = entity.GetAttributeValue<string>("wkcda_primarycontact"),
                        PrimaryContactName = entity.GetAttributeValue<string>("wkcda_firstname"),
                        PrimaryContactEmail = entity.GetAttributeValue<string>("wkcda_memberemail"),
                        PaymentToken = entity.GetAttributeValue<string>("wkcda_paymenttransaction"),
                        DonationRemarks = entity.GetAttributeValue<string>("wkcda_remarks"),
                        DonationName = entity.GetAttributeValue<string>("wkcda_giftid"),
                        DonationFrequency = GetOptionSetText(entity, "wkcda_gifttype"),
                        // DonationAmount = entity.GetAttributeValue<decimal?>("wkcda_giftamount")
                        DonationAmount = donationAmount
                    };

                    var giftDate = entity.GetAttributeValue<DateTime?>("wkcda_giftdate");
                    if (giftDate.HasValue)
                    {
                        donationTransaction.DonationReceivedDate = giftDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    donationTransactions.Add(donationTransaction);
                }

                if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID))
                {
                    retrieveInfo.CustomerID = contactMasterId;
                }
                if (string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    retrieveInfo.Email = contactEmail;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetOnlineDonationTransactionHistory");
            }

            return donationTransactions;
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

        private string GetLookupValue(Entity entity, string attributeName)
        {
            var lookup = entity.GetAttributeValue<EntityReference>(attributeName);
            if (lookup != null)
            {
                return lookup.Name ?? lookup.Id.ToString();
            }
            return null;
        }

        private bool yesnoUSCitizen(string countryValue)
        {
            if (string.IsNullOrWhiteSpace(countryValue))
                return false;

            var normalizedCountry = countryValue.Trim().ToLowerInvariant();

            var isUSCitizen = normalizedCountry == "us" ||
                       normalizedCountry == "usa" ||
                       normalizedCountry == "united states" ||
                       normalizedCountry == "united states of america" ||
                       normalizedCountry == "u.s." ||
                       normalizedCountry == "canada" ||
                       normalizedCountry == "ca" ||
                       normalizedCountry == "u.s.a.";
            return isUSCitizen;
        }
        #endregion

        #region Helper Classes

        private class ContactValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
            public EntityReference ContactRef { get; set; }
            public string ContactMasterId { get; set; }
            public string ContactEmail { get; set; }
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
            public bool Success { get; set; }
            public RetrieveInfo RetrieveInfo { get; set; }
            public string Remarks { get; set; }
            public List<DonationTransaction> DonationTransList { get; set; }
        }

        public class DonationTransaction
        {
           // public string USCitizen { get; set; }
            public bool USCitizen { get; set; }
            public string TransactionNo { get; set; }
            public string ReferenceNo { get; set; }
            public string PrimaryContactPhone { get; set; }
            public string PrimaryContactName { get; set; }
            public string PrimaryContactEmail { get; set; }
            public string PaymentToken { get; set; }
            public string DonationRemarks { get; set; }
            public string DonationReceivedDate { get; set; }
            public string DonationName { get; set; }
            public string DonationFrequency { get; set; }
            public decimal? DonationAmount { get; set; }
        }

        #endregion
    }
}