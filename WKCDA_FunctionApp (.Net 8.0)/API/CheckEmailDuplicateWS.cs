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
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class CheckEmailDuplicateWS : WS_Base
{
    public CheckEmailDuplicateWS(ILogger<CheckEmailDuplicateWS> logger) : base(logger)
    {
    }

    [Function("CheckEmailDuplicateWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/CheckEmailDuplicateWS")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        //return new OkObjectResult($"OK");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ec = GetContactsByEmail(svc, requestBody);

            var mappings = GetMappings();
            var listResults = new List<ResponseBody_Item>();
            foreach (var reqCustomer in requestBody.customers)
            {
                var contact = GetContact(ec, reqCustomer.Email);
                var isFound = contact != null;
                var resultItem = new ResponseBody_Item
                {
                    Remarks = isFound ? contact!.GetAttributeValue<string>(mappings["MasterCustomerID"]) : $"{reqCustomer.Email}: customer does not exist.",
                    Exist = isFound,
                    // ForceUpdatePassword = false
                };
                listResults.Add(resultItem);
            }


            return new OkObjectResult(listResults);
        }
        catch (JsonException ex)
        {
            var response = new ResponseBody_Item
            {
                Remarks = "Invalid Json",
                Exist = false,
            };
            return new OkObjectResult(response);
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

    /// <summary>
    /// Key     = Response field
    /// Value   = CRM field
    /// </summary>
    /// <returns></returns>
    protected override Dictionary<string, string> GetMappings()
    {
        return new Dictionary<string, string>
            {
                //API field                       CRM fields
                { "Email"                       , "emailaddress1" },
                { "MasterCustomerID"            , "wkcda_mastercustomerid" },
            };
    }

    private const string CrmEntityToSearch = "contact";
    private const string CrmFieldToSearch = "emailaddress1";

    private ColumnSet GetColumnSetFromMapping()
    {
        var mappings = GetMappings();
        var crmFields = mappings.Values.Select(o => o.ToLower())
                                       .Where(o => !string.IsNullOrEmpty(o))
                                       .ToArray();

        return new ColumnSet(crmFields);
    }

    private Entity GetContact(EntityCollection ec, string email)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>(CrmFieldToSearch) == email);
    }

    private EntityCollection GetContactsByEmail(ServiceClient svc, RequestBody requestBody)
    {
        var qe = new QueryExpression(CrmEntityToSearch);
        qe.ColumnSet = GetColumnSetFromMapping();
        qe.Criteria.AddCondition(CrmFieldToSearch, ConditionOperator.In, GetEmails(requestBody));

        return svc.RetrieveMultiple(qe);
    }

    private object[] GetEmails(RequestBody requestBody)
    {
        return requestBody.customers.Select(o => o.Email)
                                    .Distinct()
                                    .Cast<object>()
                                    .ToArray();
    }

    #region Request Class

    class RequestBody
    {
        public required RequstBody_Customer[] customers { get; set; }
    }

    class RequstBody_Customer
    {
        public required string Email { get; set; }
    }

    #endregion

    #region Response Class

    class ResponseBody_Item
    {
        public string? Remarks { get; set; }
        public bool Exist { get; set; }
        // public bool ForceUpdatePassword { get; set; }
       
    }

    #endregion
}