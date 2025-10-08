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
    public class RetrievePaymentTransactionWS : WS_Base
    {
        public RetrievePaymentTransactionWS(ILogger<RetrievePaymentTransactionWS> logger) : base(logger) { }

        [Function("RetrievePaymentTransactionWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "services/apexrest/WKCDA/RetrievePaymentTransactionWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrievePaymentTransactionWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.RetrieveInfo == null)
                    return new BadRequestObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var retrieveInfo = requestBody.RetrieveInfo;
                var response = new ResponseBody();

                // Validate that at least one identifier is provided
                if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    response.Success = false;
                    response.Remarks = "Please provide Customer ID or Email.";
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }

              /*  if (string.IsNullOrWhiteSpace(retrieveInfo.CustomerID))
                {
                    response.Success = false;
                    response.Remarks = "Customer ID is required.";
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }

                if (string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    response.Success = false;
                    response.Remarks = "Email is required.";
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }*/

                var dateValidationResult = ValidateDates(retrieveInfo.StartDate, retrieveInfo.EndDate);
                if (!dateValidationResult.IsValid)
                {
                    response.Success = false;
                    response.Remarks = dateValidationResult.Remarks;
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }

                var contactValidationResult = ValidateAndGetContact(svc, retrieveInfo);
                if (!contactValidationResult.IsValid)
                {
                    response.Success = false;
                    response.Remarks = contactValidationResult.Remarks;
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }

                int offsetNum = GetOffsetNum(retrieveInfo.PageNum);
                if (offsetNum > 2000)
                {
                    response.Success = false;
                    response.Remarks = "Beyond MAX Data rows, please change 'Start Date' and 'End Date'.";
                    response.RetrieveInfo = retrieveInfo;
                    return new BadRequestObjectResult(response);
                }

                var paymentTransList = GetPaymentTransactionHistory(svc, retrieveInfo, offsetNum, contactValidationResult.ContactRef, contactValidationResult.ContactMasterId, dateValidationResult.StartDate, dateValidationResult.EndDate);

                response.Success = true;
                response.RetrieveInfo = retrieveInfo;
                response.PaymentTransList = paymentTransList;

                if (paymentTransList.Count == 0)
                {
                    response.Remarks = "No payment transactions found for the given criteria";
                    _logger.LogInformation($"No payment transactions found. CustomerID: {retrieveInfo.CustomerID}, Email: {retrieveInfo.Email}, StartDate: {retrieveInfo.StartDate}, EndDate: {retrieveInfo.EndDate}");
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new BadRequestObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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
                EntityReference contactRef = null;
                string contactMasterId = null;
                Entity contactEntity = null;

                if (!string.IsNullOrWhiteSpace(retrieveInfo.CustomerID) && !string.IsNullOrWhiteSpace(retrieveInfo.Email))
                {
                    _logger.LogInformation($"Validating contact with both CustomerID: {retrieveInfo.CustomerID} and Email: {retrieveInfo.Email}");

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
                        contactEntity = contactResult.Entities[0];
                        var contactEmail = contactEntity.GetAttributeValue<string>("emailaddress1");

                        if (contactEmail?.Trim().ToLower() == retrieveInfo.Email.Trim().ToLower())
                        {
                            contactRef = contactEntity.ToEntityReference();
                            contactMasterId = contactEntity.GetAttributeValue<string>("wkcda_mastercustomerid");
                            var contactName = contactEntity.GetAttributeValue<string>("fullname");

                            _logger.LogInformation($"Successfully validated contact: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                            result.IsValid = true;
                            result.ContactRef = contactRef;
                            result.ContactMasterId = contactMasterId;
                        }
                        else
                        {
                            result.IsValid = false;
                            result.Remarks = $"Email {retrieveInfo.Email} does not match the provided Customer ID.";
                            _logger.LogWarning(result.Remarks);
                        }
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with MasterCustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(retrieveInfo.Email) && string.IsNullOrWhiteSpace(retrieveInfo.CustomerID))
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
                        contactEntity = contactResult.Entities[0];
                        contactRef = contactEntity.ToEntityReference();
                        contactMasterId = contactEntity.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var contactName = contactEntity.GetAttributeValue<string>("fullname");
                        var contactEmail = contactEntity.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact by email: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

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
                        contactEntity = contactResult.Entities[0];
                        contactRef = contactEntity.ToEntityReference();
                        contactMasterId = contactEntity.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var contactName = contactEntity.GetAttributeValue<string>("fullname");
                        var contactEmail = contactEntity.GetAttributeValue<string>("emailaddress1");

                        _logger.LogInformation($"Found contact by CustomerID: ID={contactRef.Id}, Name={contactName}, Email={contactEmail}, MasterID={contactMasterId}");

                        result.IsValid = true;
                        result.ContactRef = contactRef;
                        result.ContactMasterId = contactMasterId;
                    }
                    else
                    {
                        result.IsValid = false;
                        result.Remarks = $"No contact found with MasterCustomerID: {retrieveInfo.CustomerID}";
                        _logger.LogWarning(result.Remarks);
                    }
                }
                else
                {
                    result.IsValid = false;
                    result.Remarks = "Please provide either Customer ID or Email.";
                    _logger.LogWarning(result.Remarks);
                }

                return result;
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
        }

        private int GetOffsetNum(int pageNum)
        {
            int pageSize = 50;
            return (pageNum - 1) * pageSize;
        }

        private List<PaymentTransaction> GetPaymentTransactionHistory(ServiceClient svc, RetrieveInfo retrieveInfo, int offsetNum, EntityReference contactRef, string contactMasterId, DateTime? startDate = null, DateTime? endDate = null)
        {
            var paymentTransactions = new List<PaymentTransaction>();

            try
            {
                var membershipTierQuery = new QueryExpression("wkcda_membershiptierhistory")
                {
                    ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_paymenttransaction"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                {
                    new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactRef.Id)
                }
                    }
                };

                var membershipTierResult = svc.RetrieveMultiple(membershipTierQuery);
                _logger.LogInformation($"Found {membershipTierResult.Entities.Count} membership tier history records for contact");

                if (membershipTierResult.Entities.Count == 0)
                {
                    _logger.LogWarning("No membership tier history found for the contact");
                    return paymentTransactions;
                }

                var paymentTransactionIds = new List<Guid>();
                foreach (var membershipTier in membershipTierResult.Entities)
                {
                    var paymentTransactionRef = membershipTier.GetAttributeValue<EntityReference>("wkcda_paymenttransaction");
                    if (paymentTransactionRef != null)
                    {
                        paymentTransactionIds.Add(paymentTransactionRef.Id);
                    }
                }

                if (paymentTransactionIds.Count == 0)
                {
                    _logger.LogWarning("No payment transactions found in membership tier history");
                    return paymentTransactions;
                }

                var query = new QueryExpression("wkcda_paymenttransaction")
                {
                    ColumnSet = new ColumnSet(
                        "wkcda_transactiontype",
                        "wkcda_transactionno",
                        "wkcda_status",
                        "wkcda_remark",
                        "wkcda_referenceno",
                        "wkcda_paymenttype",
                        "wkcda_paymentdate",
                        "wkcda_paymentamount"
                    ),
                    PageInfo = new PagingInfo
                    {
                        PageNumber = retrieveInfo.PageNum,
                        Count = 50
                    }
                };

                query.Criteria.AddCondition(
                    "wkcda_paymenttransactionid",
                    ConditionOperator.In,
                    paymentTransactionIds.Cast<object>().ToArray()
                );

                // Add date range filter if provided
                if (startDate.HasValue && endDate.HasValue)
                {
                    var adjustedEndDate = endDate.Value.AddDays(1).AddSeconds(-1);

                      query.Criteria.AddCondition("wkcda_paymentdate", ConditionOperator.OnOrAfter, startDate.Value);
                      query.Criteria.AddCondition("wkcda_paymentdate", ConditionOperator.OnOrBefore, adjustedEndDate);

                    _logger.LogInformation($"Date filter applied: {startDate.Value:yyyy-MM-dd} to {adjustedEndDate:yyyy-MM-dd}");
                }

                _logger.LogInformation($"Executing payment transaction query for {paymentTransactionIds.Count} transaction IDs");

                var result = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Payment transaction query returned {result.Entities.Count} records");

                foreach (var entity in result.Entities)
                {
                    decimal? paymentAmount = null;
                    var moneyValue = entity.GetAttributeValue<Money>("wkcda_paymentamount");
                    if (moneyValue != null)
                    {
                        paymentAmount = moneyValue.Value;
                    }

                    string transactionType = null;
                    var transactionTypeOptionSet = entity.GetAttributeValue<OptionSetValue>("wkcda_transactiontype");
                    if (transactionTypeOptionSet != null)
                    {
                        transactionType = GetOptionSetText(entity, "wkcda_transactiontype");
                    }

                    var paymentTransaction = new PaymentTransaction
                    {
                        TransactionType = transactionType, // Use the properly handled OptionSet value
                        TransactionNo = entity.GetAttributeValue<string>("wkcda_transactionno"),
                        Status = GetOptionSetText(entity, "wkcda_status"),
                        Remarks = entity.GetAttributeValue<string>("wkcda_remark"),
                        ReferenceNo = entity.GetAttributeValue<string>("wkcda_referenceno"),
                        PaymentType = GetOptionSetText(entity, "wkcda_paymenttype"),
                        PaymentDate = entity.GetAttributeValue<DateTime?>("wkcda_paymentdate")?.ToString("yyyy-MM-dd HH:mm:ss"),
                        PaymentAmount = paymentAmount
                    };

                    paymentTransactions.Add(paymentTransaction);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetPaymentTransactionHistory");
            }

            return paymentTransactions;
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
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public RetrieveInfo RetrieveInfo { get; set; }
            public List<PaymentTransaction> PaymentTransList { get; set; }
        }

        public class PaymentTransaction
        {
            public string TransactionType { get; set; }
            public string TransactionNo { get; set; }
            public string Status { get; set; }
            public string Remarks { get; set; }
            public string ReferenceNo { get; set; }
            public string PaymentType { get; set; }
            public string PaymentDate { get; set; }
            public decimal? PaymentAmount { get; set; }
        }

        #endregion
    }
}