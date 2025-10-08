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

public class ComboMembershipDuplicationCheck : WS_Base
{
    public ComboMembershipDuplicationCheck(ILogger<ComboMembershipDuplicationCheck> logger) : base(logger) { }

    [Function("ComboMembershipDuplicationCheck")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/ComboMembershipDuplicationCheck")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.ComboMembershipDuplicationCheck");

        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();
            var rtnLst = new List<ResponseBody_Item>();
            var resultItem = new ResponseBody_Item();
            var emailSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestBody == null || requestBody.customers == null || requestBody.customers.Length == 0)
                return new BadRequestObjectResult("Input JSON is empty");
            if (requestBody.customers.Length > 1)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "only 1 customer allowed " } });
            }
            if (requestBody.customers[0].BusinessUnits.Length > 2)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Only 2 Business Units are supported for now! " } });
            }
            foreach (var cus in requestBody.customers)
            {
                resultItem.EmailAddress = cus.EmailAddress;
                var emailKey = cus.EmailAddress?.Trim().ToLowerInvariant();

                // Check for duplicate emails in the same request
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
                foreach (var item in cus.BusinessUnits)
                {
                    if (string.IsNullOrWhiteSpace(item))
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = "Please input BusinessUnit value";
                        rtnLst.Add(resultItem);
                        continue;
                    }
                    var buOptionValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptier", "wkcda_businessunit", item, _logger);
                    if (!buOptionValue.HasValue)
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = $"Invalid BusinessUnit '{item}'";
                        rtnLst.Add(resultItem);
                        continue;
                    }
                }
                
                #endregion

               

                var contact = GetContactByEmail(svc, cus.EmailAddress); // Get contact

                if (!cus.Login) // Login check
                {
                    resultItem.Success = false;
                    resultItem.Remarks = "Please login to process (Already have MyWestKowloon, website will ask user to login)";
                    rtnLst.Add(resultItem);
                    continue;
                }

                if (contact != null)
                {
                    var hasDuplicate = HasDuplicateMembershipForContact(svc, contact.Id);

                    if (hasDuplicate)
                    {
                        resultItem.Success = false;
                        resultItem.Remarks = "Already have active membership, cannot proceed to purchase (only 1 active membership allowed)";
                    }
                    else
                    {
                        resultItem.Success = true;
                        resultItem.Remarks = "Success, you are allowed to process";
                    }
                }

                rtnLst.Add(resultItem);
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
            { "", "" }
        };
    }
    private Entity? GetContactByEmail(ServiceClient svc, string email)
    {
        var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("emailaddress1") };
        qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
        return svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
    }
    private bool HasDuplicateMembershipForContact(ServiceClient svc, Guid contactId) // Check if a contact has more than 1 active membership
    {
        var qe = new QueryExpression("wkcda_membershiptierhistory")
        {
            ColumnSet = new ColumnSet("wkcda_member", "wkcda_membershipstatus"),
            Criteria = new FilterExpression
            {
                FilterOperator = LogicalOperator.And,
                Conditions =
                {
                    new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId),
                    new ConditionExpression("wkcda_membershipstatus", ConditionOperator.Equal, "Active")
                }
            }
        };

        var result = svc.RetrieveMultiple(qe);

        // If more than one record exists for the same contact with Active status, it's a duplicate
        return result.Entities.Count > 1;
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
    #region Request Classes
    class RequestBody
    {
        public required Customer[] customers { get; set; }
    }
    class Customer
    {
        public required string EmailAddress { get; set; }
        public bool Login { get; set; }
        public required string[] BusinessUnits { get; set; }

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