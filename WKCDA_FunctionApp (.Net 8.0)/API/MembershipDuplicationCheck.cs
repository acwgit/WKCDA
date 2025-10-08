using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Security.Authentication;
using System.Text.Json;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class MembershipDuplicationCheckWS : WS_Base
{
    public MembershipDuplicationCheckWS(ILogger<MembershipDuplicationCheckWS> logger) : base(logger) { }

    [Function("MembershipDuplicationCheckWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "services/apexrest/WKCDA/MembershipDuplicationCheckWS")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");

        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();
            var rtnLst = new List<ResponseBody_Item>();
            var emailSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestBody == null || requestBody.customers == null || requestBody.customers.Length == 0)
                return new BadRequestObjectResult("Input JSON is empty");

            foreach (var cus in requestBody.customers)
            {
                var resultItem = new ResponseBody_Item
                {
                    EmailAddress = cus.EmailAddress,
                    Success = true
                };
                var emailKey = cus.EmailAddress?.Trim().ToLowerInvariant();

                if (cus.Renewal.GetValueOrDefault() && cus.Upgrade)
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Renewal and Upgrade cannot both be true!";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (!string.IsNullOrEmpty(emailKey) && emailSet.Contains(emailKey))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = $"You have duplicated Email Address in your input: {emailKey}";
                    rtnLst.Add(resultItem);
                    continue;
                }
                emailSet.Add(emailKey);

                #region Validations
                if (string.IsNullOrWhiteSpace(cus.EmailAddress))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Please input Email value";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (!IsValidEmail(cus.EmailAddress))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Invalid Email format";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cus.BusinessUnit))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Please input BusinessUnit value";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cus.MembershipTier))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Please input MembershipTier value";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (cus.Upgrade && string.IsNullOrWhiteSpace(cus.MemberTierHistoryId))
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "MemberTierHistoryId value cannot be empty for Upgrade";
                    rtnLst.Add(resultItem);
                    continue;
                }
                #endregion

                rtnLst.Add(resultItem);
            }

            for (int i = 0; i < requestBody.customers.Length; i++)
            {
                var cus = requestBody.customers[i];
                var resultItem = rtnLst[i];

                if (resultItem.Success == false)
                    continue;

               var contact = GetContactByEmail(svc, cus.EmailAddress);
                if (contact == null)
                 {
                     resultItem.Success = false;
                     resultItem.Remarks = "Email not found in the records";
                     continue;
                 }

                 if (!IsValidBusinessUnit(svc, cus.BusinessUnit))
               {
                   resultItem.Success = false;
                   resultItem.Remarks = $"Invalid BusinessUnit '{cus.BusinessUnit}' - BU does not exist";
                   continue;
               }

               if (!IsValidMembershipTier(svc, cus.MembershipTier))
               {
                   resultItem.Success = false;
                   resultItem.Remarks = $"MembershipTier: '{cus.MembershipTier}' does not exist.";
                   continue;
               }

                if (!cus.Login)
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "please login to process (Already have MyWestKowloon, website will ask user to login)";
                    continue;
                }

                var hasDuplicate = HasDuplicateMembershipForContact(svc, contact.Id, cus.BusinessUnit);
                var existingRenewableMembership = GetRenewableMembershipForContact(svc, contact.Id, cus.BusinessUnit);

                if (cus.Renewal.GetValueOrDefault())
                {
                    if (existingRenewableMembership != null)
                    {
                        resultItem.Success = true;
                        resultItem.Remarks = "Success, you are allowed to renew";
                    }
                    else
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = "Not eligible for renewal";
                    }
                }
                else if (cus.Upgrade)
                {
                    var upgradeValidation = ValidateMembershipForUpgrade(svc, cus.MemberTierHistoryId, contact.Id);
                    if (upgradeValidation.IsValid)
                    {
                        resultItem.Success = true;
                        resultItem.Remarks = "Success, you are allowed to upgrade";
                    }
                    else
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = upgradeValidation.ErrorMessage;
                    }
                }
                else
                {
                    if (hasDuplicate)
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = "Already have active membership, cannot proceed to purchase (Already have active membership, cannot proceed to purchase as only 1 active membership is allowed)";
                    }
                    else
                    {
                        resultItem.Success = true;
                        resultItem.Remarks = "Success, you are allowed to process";
                    }
                }
            }

            return new OkObjectResult(rtnLst);
        }
        catch (JsonException)
        {
            return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" });
        }
        catch (AuthenticationException ex)
        {
            return new UnauthorizedObjectResult(ex.Message);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("unauthorized"))
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
            { "", "" }
        };
    }

    private Entity? GetContactByEmail(ServiceClient svc, string email)
    {
        var qe = new QueryExpression("contact")
        {
            ColumnSet = new ColumnSet("contactid", "emailaddress1", "firstname", "lastname")
        };
        qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
        return svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
    }

    private bool HasDuplicateMembershipForContact(ServiceClient svc, Guid contactId, string businessUnit)
    {
        int? buOptionValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptier", "wkcda_businessunit", businessUnit, _logger);

        if (!buOptionValue.HasValue)
        {
            _logger.LogWarning("Invalid business unit: {BusinessUnit}", businessUnit);
            return false;
        }

        string buStringValue = GetBusinessUnitStringValue(buOptionValue.Value);

        var qe = new QueryExpression("wkcda_membershiptierhistory")
        {
            ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid"),
            Criteria = new FilterExpression(LogicalOperator.And)
        };

       /* var tierLink = qe.AddLink("wkcda_membershiptier", "wkcda_membershiptier", "wkcda_membershiptierid");
        tierLink.Columns = new ColumnSet("wkcda_businessunit", "wkcda_paidmembership");
        tierLink.EntityAlias = "tier";*/

        qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
        qe.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
        qe.Criteria.AddCondition("wkcda_membershipstatus", ConditionOperator.Equal, "Active");
      //  qe.Criteria.AddCondition("tier", "wkcda_businessunit", ConditionOperator.Equal, buOptionValue.Value);

        qe.Criteria.AddCondition("wkcda_businessunit", ConditionOperator.Equal, buStringValue);
        //qe.Criteria.AddCondition("tier", "wkcda_paidmembership", ConditionOperator.Equal, true); // Paid membership only

        var result = svc.RetrieveMultiple(qe);
        return result.Entities.Count > 0;
    }

    private Entity? GetRenewableMembershipForContact(ServiceClient svc, Guid contactId, string businessUnit)
    {
        int? buOptionValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptier", "wkcda_businessunit", businessUnit, _logger);

        if (!buOptionValue.HasValue)
        {
            _logger.LogWarning("Invalid business unit: {BusinessUnit}", businessUnit);
            return null;
        }

        string buStringValue = GetBusinessUnitStringValue(buOptionValue.Value);

        var qe = new QueryExpression("wkcda_membershiptierhistory")
        {
            ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_renewalpaidmembership", "wkcda_membershiptier", "wkcda_member", "statecode", "wkcda_membershipstatus"),
            Criteria = new FilterExpression(LogicalOperator.And)
        };

        qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
        qe.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
        qe.Criteria.AddCondition("wkcda_membershipstatus", ConditionOperator.Equal, "Active");
        qe.Criteria.AddCondition("wkcda_renewalpaidmembership", ConditionOperator.Equal, false);
        qe.Criteria.AddCondition("wkcda_businessunit", ConditionOperator.Equal, buStringValue);
       // qe.Criteria.AddCondition("wkcda_paidmembership", ConditionOperator.Equal, true); // Paid membership only

        var result = svc.RetrieveMultiple(qe);
        return result.Entities.FirstOrDefault();
    }

    private string GetBusinessUnitStringValue(int optionSetValue)
    {
        return optionSetValue switch
        {
            372120000 => "WKCD",
            372120001 => "M+",
            372120002 => "HKPM",
            372120003 => "PA",
            _ => "Unknown"
        };
    }

    private UpgradeValidationResult ValidateMembershipForUpgrade(ServiceClient svc, string memberTierHistoryId, Guid contactId)
    {
        if (!Guid.TryParse(memberTierHistoryId, out Guid mthId))
        {
            return new UpgradeValidationResult { IsValid = false, ErrorMessage = "Invalid MemberTierHistoryId format" };
        }

        try
        {
            var qe = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_member", "statecode", "wkcda_membershipstatus"),
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            qe.Criteria.AddCondition("wkcda_membershiptierhistoryid", ConditionOperator.Equal, mthId);
            qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
            qe.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // Active state
            qe.Criteria.AddCondition("wkcda_membershipstatus", ConditionOperator.Equal, "Active");

            var result = svc.RetrieveMultiple(qe);
            var membership = result.Entities.FirstOrDefault();

            if (membership == null)
            {
                return new UpgradeValidationResult { IsValid = false, ErrorMessage = "Membership not found or not eligible for upgrade" };
            }

            return new UpgradeValidationResult { IsValid = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating membership for upgrade: {MemberTierHistoryId}", memberTierHistoryId);
            return new UpgradeValidationResult { IsValid = false, ErrorMessage = "Error validating membership for upgrade" };
        }
    }

    private bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidBusinessUnit(ServiceClient svc, string businessUnit)
    {
        try
        {
            int? buOptionValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptier", "wkcda_businessunit", businessUnit, _logger);
            return buOptionValue.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating business unit: {BusinessUnit}", businessUnit);
            return false;
        }
    }

    private bool IsValidMembershipTier(ServiceClient svc, string membershipTierName)
    {
        try
        {
            var query = new QueryExpression("wkcda_membershiptier")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptiername"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_membershiptiername", ConditionOperator.Equal, membershipTierName)
                    }
                },
                TopCount = 1
            };

            var result = svc.RetrieveMultiple(query);
            return result.Entities.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating membership tier: {MembershipTier}", membershipTierName);
            return false;
        }
    }

    #region Helper Classes
    private class UpgradeValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
    #endregion

    #region Request Classes
    class RequestBody
    {
        public required Customer[] customers { get; set; }
    }

    class Customer
    {
        public required string EmailAddress { get; set; }
        public required string BusinessUnit { get; set; }
        public required string MembershipTier { get; set; }
        public bool Login { get; set; }
        public bool? Renewal { get; set; }
        public bool Upgrade { get; set; }
        public string? MemberTierHistoryId { get; set; } // Added for upgrade scenarios
    }
    #endregion

    #region Response Class
    class ResponseBody_Item
    {
        public bool Success { get; set; }
        public string? Remarks { get; set; }
        public string EmailAddress { get; set; }

    }
    #endregion
}