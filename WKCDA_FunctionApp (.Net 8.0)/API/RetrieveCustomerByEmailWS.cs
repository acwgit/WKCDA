using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.ServiceModel.Security;
using System.Text.Json;
using Azure;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class RetrieveCustomerByEmailWS : WS_Base
{
    public RetrieveCustomerByEmailWS(ILogger<RetrieveCustomerByEmailWS> logger) : base(logger)
    {
    }

    [Function("RetrieveCustomerByEmailWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveCustomerByEmailWS")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        //return new OkObjectResult($"OK");
        var listResults = new List<ResponseBody_Item>();
        try
        {
           
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ec = GetContactsByEmail(svc, requestBody);

            var mappings = GetMappings();
           
            foreach (var reqCustomer in requestBody.customers)
            {
                var contact = GetContact(ec, reqCustomer.Email);
                var isFound = contact != null;
                var resultItem = new ResponseBody_Item
                {
                    Remarks = isFound ? reqCustomer.Email : $"{reqCustomer.Email}: customer does not exist.",
                    Success = isFound,
                    customer = isFound ? new ResponseBody_Customer(contact, mappings) : null
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
                Success = false,
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

        //catch (Exception ex)
        //{
        //    throw;
        //}
    }

    private IActionResult StatusCode(int internalServerError, string v)
    {
        throw new NotImplementedException();
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
                { "YearOfBirth"                 , "wkcda_yearofbirth" },
                { "wk_OfficePhoneNumber"        , "wkcda_wkmeofficephonenumber" },
                { "wk_OfficePhoneCountryCode"   , "wkcda_wkmeofficephonecountrycode" },
                { "wk_HomePhoneNumber"          , "wkcda_wkmehomephonenumber" },
                { "wk_HomePhoneCountryCode"     , "wkcda_wkmehomephonecountrycode" },
                { "WK_eNews"                    , "wkcda_westkowloonenewsletter" },
                { "WeChatID"                    , "wkcda_wechatid" },
                { "UnsubscribeAll"              , "wkcda_unsubscribeall" },
                { "Title"                       , "wkcda_title" },
                { "TicketingPatronAccountNo"    , "wkcda_ticketingpatronaccountno" },
                //subscriptionInfos
                { "Status"                      , "wkcda_status" },
                { "SalutationChi"               , "wkcda_salutationchi" },
                { "Salutation"                  , "wkcda_salutation" },
                //referenceno
                //QuickMode
                { "PreferredLanguage"           , "wkcda_preferredlanguage" },
                { "PreferredContactMethod"      , "preferredcontactmethodcode" },
                //Password
                { "OptOutDate5"                 , "wkcda_emailoptoutdate5" },
                { "OptInDate5"                  , "wkcda_emailoptindate5" },
                { "OptInChannel5"               , "wkcda_optinchannel5" },
                { "OptInChannel4"               , "wkcda_optinchannel4" },
                { "OptInChannel2"               , "wkcda_optinchannel2" },
                { "OptInChannel1"               , "wkcda_optinchannel1" },
                //NeedEligibilytyCheckCheck
                //{ "NeedEligibilityCheck"        , "wkcda_needeligibilitycheck" },
                { "Mplus_Membership_eNews"      , "wkcda_mmembershipenews" },
                { "MPlus_eNews"                 , "wkcda_mmagazine" },
                { "MonthofBirth"                , "wkcda_monthofbirth" },
                { "MobilePhoneNumber"           , "wkcda_wkmemobilephonenumber" },
                { "MobilePhoneCountryCode"      , "wkcda_wkmemobilephonecountrycode" },
                { "MobileCountryCode"           , "wkcda_mobilecountrycode" },
                { "Mobile"                      , "mobilephone" },
                //MembershipTier
                //MembershipSource
                { "MemberId"                    , "wkcda_memberid" },
                { "MasterCustomerID"            , "wkcda_mastercustomerid" },
                { "MaritalStatus"               , "familystatuscode" },
                { "LineID"                      , "wkcda_lineid" },
                { "LastNameChi"                 , "wkcda_lastnamechi" },
                { "LastName"                    , "lastname" },
                { "IsSubsidaryAccount"          , "wkcda_IsSubsidiaryAccount" },
                //IsStudent
                //IsOverrideName
                //IsChild
                //InterestStep
                { "Interest"                    , "wkcda_interest" },
                { "HomePhone"                   , "telephone2" },
                //HKPM_eNews
                { "Gender"                      , "gendercode" },
                { "FirstNameChi"                , "wkcda_firstnamechi" },
                { "FirstName"                   , "firstname" },
                { "EmailOptOutDate4"            , "wkcda_emailoptoutdate2" },
                { "EmailOptOutDate2"            , "wkcda_emailoptoutdate2" },
                { "EmailOptOutDate1"            , "wkcda_emailoptoutdate1" },
                { "EmailOptinDate4"             , "wkcda_emailoptindate4" },
                { "EmailOptinDate2"             , "wkcda_emailoptindate2" },
                { "EmailOptinDate1"             , "wkcda_emailoptindate1" },
                { "Email"                       , "emailaddress1" },
                { "Department"                  , "department" },
                { "DeliveryMethod"              , "wkcda_ticketdeliverymethod" },
                { "DeliveryAddressCountry"      , "wkcda_deliveryaddresscountry" },
                { "DeliveryAddress3"            , "wkcda_deliveryaddressline3" },
                { "DeliveryAddress2"            , "wkcda_deliveryaddressline2" },
                { "DeliveryAddress1"            , "wkcda_deliveryaddressline1" },
                { "DateOfBirth"                 , "birthdate" },
                { "CustomerSource"              , "wkcda_customersource" },
                { "CustomerInterestSelected"    , "wkcda_customerinterestselected" },
                { "Company"                     , "wkcda_companyinput" },
                { "BillingAddressCountry"       , "wkcda_billingaddresscountry" },
                { "BillingAddress3"             , "wkcda_billingaddressline3" },
                { "BillingAddress2"             , "wkcda_billingaddressline2" },
                { "BillingAddress1"             , "wkcda_billingaddressline1" },
                { "ArtForm"                     , "wkcda_artform" },
                { "AltCntcMobileCountryCode"    , "wkcda_alternatecontactmobilecountrycode" },
                { "AltCntcMobile"               , "wkcda_alternatecontactmobile" },
                { "AltCntcLastName"             , "wkcda_alternatecontactpersonlastname" },
                { "AltCntcFirstName"            , "wkcda_alternatecontactpersonfirstname" },
                { "AltCntcEmail"                , "wkcda_alternatecontactpersonalemail" },
                { "AddressCountry"              , "wkcda_primaryaddresscountry" },
                { "Address3"                    , "wkcda_primaryaddressline3" },
                { "Address2"                    , "wkcda_primaryaddressline2" },
                { "Address1"                    , "wkcda_primaryaddressline1" },
                { "FirstLoginIndicator"         , "wkcda_firstloginindicator" },
                { "ExternalEntraIDObjectID"     , "wkcda_externalentraidobjectid" }

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
        public bool Success { get; set; }
        public string? Remarks { get; set; }
       public bool Updated { get; set; }
       public string? MonthofBirth { get; set; }
       public string? MembershipTierName { get; set; }
       public string? MembershipStatus { get; set; }
       public string? GroupType { get; set; }
       public ResponseBody_Customer? customer { get; set; }
      
       
    }
   

    public class ResponseBody_Customer : Contact_Base
    {
        public ResponseBody_Customer(Entity contact, Dictionary<string, string> mappings) : base(contact, mappings)
        {
        }

        new public string AltCntcFirstName { get; set; }
        new public string AltCntcLastName { get; set; }
        new public string AltCntcEmail { get; set; }
        new public string AltCntcMobileCountryCode { get; set; }
        new public string AltCntcMobile { get; set; }
        new public string SubscriptionInfos = null;
        new public bool NeedEligibilityCheckCheck = false;
        new public string NeedEligibilityCheck = null;
        new public string ReferenceNo = null;
        new public bool QuickMode = false;
        new public string Password = null;
        new public string MembershipTierName = null;
        new public string MembershipSource = null;
        new public string IsStudent = null;
        new public string IsOverrideName = null;
        new public string IsChild = null;
        new public string InterestStep = null;
        new public bool HKPM_eNews = false;

    }

    #endregion
}