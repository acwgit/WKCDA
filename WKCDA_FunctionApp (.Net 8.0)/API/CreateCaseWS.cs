using Microsoft.AspNetCore.Http;
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
using System.Text.Json.Serialization;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class CreateCaseWS : WS_Base
{
    public CreateCaseWS(ILogger<CreateCaseWS> logger) : base(logger)
    {
    }

    [Function("CreateCaseWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/CreateCaseWs")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var mappings = GetMappings();
            var response = new ResponseBody
            {
                Success = false,
                Remarks = "",
                CaseReference = null
            };

            #region Validations
            {
                if (requestBody == null || requestBody.inputData == null)
                {
                    response.Remarks = "Invalid request body";
                    return new OkObjectResult(response);
                }

                var inputData = requestBody.inputData;
                if (string.IsNullOrEmpty(inputData.Email))
                {
                    response.Remarks = "Email is mandatory";
                    return new OkObjectResult(response);
                }

                if (string.IsNullOrEmpty(inputData.Subject))
                {
                    response.Remarks = "Subject is mandatory";
                    return new OkObjectResult(response);
                }

                if (string.IsNullOrEmpty(inputData.Description))
                {
                    response.Remarks = "Description is mandatory";
                    return new OkObjectResult(response);
                }
            }
            #endregion

            try
            {
                var inputData = requestBody.inputData;
                #region Create Case
                var caseEntity = new Entity("incident");
                caseEntity["title"] = inputData.Subject;
                caseEntity["description"] = inputData.Description;
                caseEntity["wkcda_contactemail"] = inputData.Email;
                caseEntity["wkcda_contactsalutation"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "incident", "wkcda_contactsalutation", inputData.Salutation, _logger) ?? 0);
                caseEntity["wkcda_contactfirstname"] = inputData.FirstName;
                caseEntity["wkcda_contactlastname"] = inputData.LastName;
                caseEntity["wkcda_contactphone"] = inputData.PhoneCountryCode + " " + inputData.Phone;
                caseEntity["casetypecode"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "incident", "casetypecode", inputData.Type, _logger) ?? 0);
                caseEntity["wkcda_iscreatingpics"] = inputData.isCreatingPICS;
                caseEntity["caseorigincode"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "incident", "caseorigincode", inputData.CaseOrigin, _logger) ?? 0);

                // query dummy contact
                var customer = GetCustomerReference(svc, "dummy");
                if (customer == null)
                {
                    throw new Exception("No dummy contact for Create Case");
                }

                caseEntity["customerid"] = GetCustomerReference(svc, "dummy");

                // Create the case record
                var caseId = svc.Create(caseEntity);

                // Retrieve the created case to get the case reference number
                var retrievedCase = svc.Retrieve(Incident.EntityLogicalName, caseId, new ColumnSet("ticketnumber"));
                var caseReference = retrievedCase.GetAttributeValue<string>("ticketnumber");

                response.Success = true;
                response.Remarks = "Success";
                response.CaseReference = caseReference;

                #endregion
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
            //catch (Exception ex)
            //{
            //    response.Success = false;
            //    response.Remarks = ex.Message;
            //    _logger.LogError(ex, "Error creating case");
            //}

            return new OkObjectResult(response);
        }
        catch (JsonException)
        {
            var response = new ResponseBody
            {
                Success = false,
                Remarks = "Invalid Json",
                CaseReference = null
            };
            return new OkObjectResult(response);
        }
        catch (AuthenticationException ex)
        {
            return new UnauthorizedObjectResult(ex.Message);
        }
        catch (Exception ex)
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
                { "Email"                       , "wkcda_contactemail" },
                { "Salutation"                  , "wkcda_contactsalutation" },
                { "FirstName"                   , "wkcda_contactfirstname" },
                { "LastName"                    , "wkcda_contactlastname" },
                { "Phone"                       , "wkcda_contactphone" },
                { "Type"                        , "casetypecode" },
                { "Subject"                     , "title" },
                { "Description"                 , "description" },
                { "IsCreatingPICS"              , "wkcda_iscreatingpics" },
                { "Customer"                    , "customerid" },
                { "CaseOrigin"                  , "caseorigincode" },
            };
    }

    private EntityReference? GetCustomerReference(ServiceClient svc, string lastname)
    {
        var query = new QueryExpression("contact")
        {
            ColumnSet = new ColumnSet("contactid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("lastname", ConditionOperator.Equal, lastname)
                }
            }
        };
        var result = svc.RetrieveMultiple(query);
        if (result.Entities.Count > 0)
        {
            return new EntityReference("contact", result.Entities[0].Id);
        }
        return null;
    }


    #region Request Class

    class RequestBody
    {
        public required InputData inputData { get; set; }
    }

    class InputData
    {
        public string Email { get; set; }
        public string Salutation { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public string PhoneCountryCode { get; set; }
        public string Phone { get; set; }
        public required string Subject { get; set; }
        public required string Description { get; set; }
        public string Type { get; set; }
        public bool isCreatingPICS { get; set; }
        public string CaseOrigin { get; set; }
    }

    #endregion

    #region Response Class

    class ResponseBody
    {
        public bool Success { get; set; }
        public required string Remarks { get; set; }
        public string? CaseReference { get; set; }
    }

    #endregion
}