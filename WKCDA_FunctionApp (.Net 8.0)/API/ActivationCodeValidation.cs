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
using System.Linq;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class ActivationCodeValidationWS : WS_Base
    {
        public ActivationCodeValidationWS(ILogger<ActivationCodeValidationWS> logger) : base(logger) { }

        [Function("ActivationCodeValidation")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/ActivationCodeValidation")] HttpRequest req)
        {
            _logger.LogInformation("APIName: ActivationCodeValidation");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                // Input validation
                var inputValidation = ValidateRequestBody(requestBody);
                if (!inputValidation.Success)
                {
                    return new OkObjectResult(new List<ResponseBody> {
                        new ResponseBody {
                            Success = false,
                            Remarks = inputValidation.Message
                        }
                    });
                }

                var response = ValidateActivationCodes(svc, requestBody.InputCodes);
                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody> { new ResponseBody { Success = false, Remarks = "Invalid JSON" } });
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
                _logger.LogError(ex, "Error retrieving group accounts");
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
                   ex.InnerException != null && IsUnauthorizedException(ex.InnerException);
        }

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region Validation Methods

        private ValidationResponse ValidateRequestBody(RequestBody requestBody)
        {
            if (requestBody == null)
            {
                return new ValidationResponse
                {
                    Success = false,
                    Message = "Request body cannot be null"
                };
            }

            if (requestBody.InputCodes == null || requestBody.InputCodes.Count == 0)
            {
                return new ValidationResponse
                {
                    Success = false,
                    Message = "InputCodes cannot be empty"
                };
            }

            foreach (var inputCode in requestBody.InputCodes)
            {
                if (inputCode == null)
                {
                    return new ValidationResponse
                    {
                        Success = false,
                        Message = "Input code object cannot be null"
                    };
                }

                if (string.IsNullOrWhiteSpace(inputCode.ValidationCode))
                {
                    return new ValidationResponse
                    {
                        Success = false,
                        Message = "ValidationCode cannot be null or empty"
                    };
                }

                if (string.IsNullOrWhiteSpace(inputCode.CodeType))
                {
                    return new ValidationResponse
                    {
                        Success = false,
                        Message = "CodeType cannot be null or empty"
                    };
                }

                if (inputCode.CodeType != "ActivationCode" && inputCode.CodeType != "PhysicalCardSerialNumber")
                {
                    return new ValidationResponse
                    {
                        Success = false,
                        Message = $"CodeType '{inputCode.CodeType}' is not supported. Supported types: ActivationCode, PhysicalCardSerialNumber"
                    };
                }

                if (inputCode.ValidationCode.Trim().Length == 0)
                {
                    return new ValidationResponse
                    {
                        Success = false,
                        Message = "ValidationCode cannot be only whitespace"
                    };
                }
            }

            return new ValidationResponse { Success = true };
        }

        #endregion

        #region CRM Helpers

        private List<ResponseBody> ValidateActivationCodes(ServiceClient svc, List<InputCode> inputCodes)
        {
            var response = new List<ResponseBody>();

            try
            {
                var validationResult = ValidateInputCodes(inputCodes);
                if (!validationResult.IsValid)
                {
                    response.Add(new ResponseBody
                    {
                        Success = false,
                        Remarks = validationResult.Remarks
                    });
                    return response;
                }

                var activationCodes = new List<string>();
                var cardSerialNumbers = new List<string>();
                var codeMap = new Dictionary<string, InputCode>();

                foreach (var inputCode in inputCodes)
                {
                    var normalizedCode = inputCode.ValidationCode?.ToUpper();
                    var key = $"{normalizedCode}{inputCode.CodeType}";

                    if (inputCode.CodeType == "ActivationCode")
                    {
                        activationCodes.Add(normalizedCode);
                    }
                    else if (inputCode.CodeType == "PhysicalCardSerialNumber")
                    {
                        cardSerialNumbers.Add(normalizedCode);
                    }

                    codeMap[key] = inputCode;
                }

                var activationCodeValidation = CheckActivationCodes(svc, activationCodes, cardSerialNumbers, inputCodes.Count);
                if (!activationCodeValidation.IsValid)
                {
                    response.Add(new ResponseBody
                    {
                        Success = false,
                        Remarks = activationCodeValidation.Remarks
                    });
                    return response;
                }

                response = GetMembershipTierHistories(svc, activationCodes, cardSerialNumbers, codeMap);

                if (response.Count == 0)
                {
                    response.Add(new ResponseBody
                    {
                        Success = false,
                        Remarks = "No membership records found for the provided activation codes"
                    });
                }

                return response;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                return response;
            }
        }

        private ValidationResult ValidateInputCodes(List<InputCode> inputCodes)
        {
            var result = new ValidationResult();

            if (inputCodes == null || inputCodes.Count == 0)
            {
                result.IsValid = false;
                result.Remarks = "InputCodes cannot be empty!";
                return result;
            }

            foreach (var code in inputCodes)
            {
                if (string.IsNullOrWhiteSpace(code.ValidationCode))
                {
                    result.IsValid = false;
                    result.Remarks = "ValidationCode cannot be blank!";
                    return result;
                }

                if (code.CodeType != "ActivationCode" && code.CodeType != "PhysicalCardSerialNumber")
                {
                    result.IsValid = false;
                    result.Remarks = $"CodeType Not Supported: {code.CodeType}";
                    return result;
                }
            }

            result.IsValid = true;
            return result;
        }

        private ActivationCodeValidationResult CheckActivationCodes(ServiceClient svc, List<string> activationCodes, List<string> cardSerialNumbers, int inputCount)
        {
            var result = new ActivationCodeValidationResult();

            try
            {
                var query = new QueryExpression("wkcda_membershipactivation");
                query.ColumnSet = new ColumnSet("wkcda_membershipactivationid", "wkcda_codestatus", "wkcda_issuedate", "wkcda_activationcode", "wkcda_physicalcardserialnumber");

                var filter = new FilterExpression(LogicalOperator.Or);

                if (activationCodes.Count > 0)
                {
                    filter.AddCondition("wkcda_activationcode", ConditionOperator.In, activationCodes.ToArray());
                }

                if (cardSerialNumbers.Count > 0)
                {
                    filter.AddCondition("wkcda_physicalcardserialnumber", ConditionOperator.In, cardSerialNumbers.ToArray());
                }

                query.Criteria = filter;

                var activationRecords = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Found {activationRecords.Entities.Count} activation records");

                if (activationRecords.Entities.Count == 0)
                {
                    result.IsValid = false;
                    result.Remarks = "No activation code records were found!";
                    return result;
                }

                if (activationRecords.Entities.Count != inputCount)
                {
                    result.IsValid = false;
                    result.Remarks = $"Found activation code record count {activationRecords.Entities.Count}, is not matching the input count {inputCount}";
                    return result;
                }

                foreach (var entity in activationRecords.Entities)
                {
                    var codeStatus = GetOptionSetText(entity, "wkcda_codestatus");
                    var activationCode = entity.GetAttributeValue<string>("wkcda_activationcode");
                    var cardNumber = entity.GetAttributeValue<string>("wkcda_physicalcardserialnumber");

                    _logger.LogInformation($"Checking activation record: Code={activationCode}, Card={cardNumber}, Status={codeStatus}");

                    if (codeStatus == "Invalid")
                    {
                        result.IsValid = false;
                        result.Remarks = $"Activation code '{activationCode}' is invalid!";
                        return result;
                    }
                    else if (codeStatus == "Activated")
                    {
                        result.IsValid = false;
                        result.Remarks = $"Activation code '{activationCode}' has already been activated!";
                        return result;
                    }
                    else if (codeStatus == "Issued")
                    {
                        var issueDate = entity.GetAttributeValue<DateTime?>("wkcda_issuedate");
                        if (issueDate.HasValue && issueDate.Value.AddDays(90) < DateTime.Today)
                        {
                            result.IsValid = false;
                            result.Remarks = $"Activation code '{activationCode}' has expired!";
                            return result;
                        }
                    }
                }

                result.IsValid = true;
                result.ActivationRecords = activationRecords;
                return result;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                return result;

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

        private List<ResponseBody> GetMembershipTierHistories(ServiceClient svc, List<string> activationCodes, List<string> cardSerialNumbers, Dictionary<string, InputCode> codeMap)
        {
            var results = new List<ResponseBody>();
            var resultMap = new Dictionary<string, ResponseBody>();

            try
            {
                var query = new QueryExpression("wkcda_membershiptierhistory");
                query.ColumnSet = new ColumnSet(
                    "wkcda_membershiptierhistoryid",
                    "wkcda_grouptype",
                    "wkcda_membershipgroup",
                    "wkcda_membershiptier",
                    "wkcda_membershiprole",
                    "wkcda_paymenttransaction",
                    "wkcda_membershipactivation"
                );

                var activationLink = new LinkEntity("wkcda_membershiptierhistory", "wkcda_membershipactivation", "wkcda_membershipactivation", "wkcda_membershipactivationid", JoinOperator.Inner);
                activationLink.Columns = new ColumnSet("wkcda_activationcode", "wkcda_physicalcardserialnumber");
                activationLink.EntityAlias = "activation";

                var linkFilter = new FilterExpression(LogicalOperator.Or);

                if (activationCodes.Count > 0)
                {
                    linkFilter.AddCondition("wkcda_activationcode", ConditionOperator.In, activationCodes.ToArray());
                }

                if (cardSerialNumbers.Count > 0)
                {
                    linkFilter.AddCondition("wkcda_physicalcardserialnumber", ConditionOperator.In, cardSerialNumbers.ToArray());
                }

                activationLink.LinkCriteria = linkFilter;
                query.LinkEntities.Add(activationLink);

                var tierHistories = svc.RetrieveMultiple(query);
                _logger.LogInformation($"Found {tierHistories.Entities.Count} membership tier history records");

                if (tierHistories.Entities.Count == 0)
                {
                    return results;
                }

                foreach (var entity in tierHistories.Entities)
                {
                    var activationCode = entity.GetAttributeValue<AliasedValue>("activation.wkcda_activationcode")?.Value as string;
                    var cardNumber = entity.GetAttributeValue<AliasedValue>("activation.wkcda_physicalcardserialnumber")?.Value as string;

                    _logger.LogInformation($"Processing tier history: ActivationCode={activationCode}, CardNumber={cardNumber}");

                    string codeKey = null;
                    ResponseBody result = null;

                    if (!string.IsNullOrWhiteSpace(activationCode))
                    {
                        codeKey = $"{activationCode.ToUpper()}ActivationCode";
                        if (codeMap.ContainsKey(codeKey) && resultMap.ContainsKey(codeKey))
                        {
                            result = resultMap[codeKey];
                        }
                    }

                    if (result == null && !string.IsNullOrWhiteSpace(cardNumber))
                    {
                        codeKey = $"{cardNumber.ToUpper()}PhysicalCardSerialNumber";
                        if (codeMap.ContainsKey(codeKey) && resultMap.ContainsKey(codeKey))
                        {
                            result = resultMap[codeKey];
                        }
                    }

                    if (result == null)
                    {
                        result = new ResponseBody
                        {
                            ValidationCode = activationCode,
                            Success = true,
                            AccountProfile = new List<AccountProfile>()
                        };

                        if (!string.IsNullOrWhiteSpace(activationCode))
                        {
                            codeKey = $"{activationCode.ToUpper()}ActivationCode";
                        }
                        else if (!string.IsNullOrWhiteSpace(cardNumber))
                        {
                            codeKey = $"{cardNumber.ToUpper()}PhysicalCardSerialNumber";
                        }

                        if (codeKey != null)
                        {
                            resultMap[codeKey] = result;
                            _logger.LogInformation($"Created new result for code key: {codeKey}");
                        }
                    }

                    var accountProfile = retAccountProfile(entity);
                    result.AccountProfile.Add(accountProfile);

                    if (string.IsNullOrEmpty(result.GroupId))
                    {
                        var groupReference = entity.GetAttributeValue<EntityReference>("wkcda_membershipgroup");
                        if (groupReference != null)
                        {
                            result.GroupId = groupReference.Id.ToString();
                            result.MemberGroupType = entity.GetAttributeValue<string>("wkcda_grouptype");
                            _logger.LogInformation($"Set group info: GroupId={result.GroupId}, GroupType={result.MemberGroupType}");
                        }
                    }
                }

                results.AddRange(resultMap.Values);
                _logger.LogInformation($"Returning {results.Count} validation results");
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving membership tier histories");
                return results;
            }
        }

        private AccountProfile retAccountProfile(Entity entity)
        {
            var tierReference = entity.GetAttributeValue<EntityReference>("wkcda_membershiptier");
            var tierName = tierReference?.Name;
            var paymentReference = entity.GetAttributeValue<EntityReference>("wkcda_paymenttransaction");
            var membershipRole = GetOptionSetText(entity, "wkcda_membershiprole");

            return new AccountProfile
            {
                MemberTierHistoryId = entity.Id.ToString(),
                MemberTierName = tierName,
                PaymentHistoryId = paymentReference?.Id.ToString(),
                MemberGroupRole = membershipRole
            };
        }

        #endregion

        #region Helper Classes

        private class ValidationResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; }
        }

        private class ValidationResult
        {
            public bool IsValid { get; set; }
            public string Remarks { get; set; }
        }

        private class ActivationCodeValidationResult
        {
            public bool IsValid { get; set; }
            public string validationCode { get; set; }
            public string Remarks { get; set; }
            public EntityCollection ActivationRecords { get; set; }
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public List<InputCode> InputCodes { get; set; }
        }

        public class InputCode
        {
            public string ValidationCode { get; set; }
            public string CodeType { get; set; }
        }

        public class ResponseBody
        {
            public string ValidationCode { get; set; }
            public bool Success { get; set; }
            public string GroupId { get; set; }
            public string MemberGroupType { get; set; }
            public List<AccountProfile> AccountProfile { get; set; }
            public string Remarks { get; set; }
        }

        public class AccountProfile
        {
            public string MemberTierHistoryId { get; set; }
            public string MemberTierName { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberGroupRole { get; set; }
        }

        #endregion
    }
}