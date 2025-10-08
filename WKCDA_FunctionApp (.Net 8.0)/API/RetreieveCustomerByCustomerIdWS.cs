using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using static WKCDA_FunctionApp__.Net_8._0_.API.RetrieveCustomerByEmailWS;


namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class RetreieveCustomerByCustomerIdWS : WS_Base
{
    public RetreieveCustomerByCustomerIdWS(ILogger<RetreieveCustomerByCustomerIdWS> logger) : base(logger)
    {
    }

    [Function("RetreieveCustomerByCustomerIdWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetreieveCustomerByCustomerIdWS")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        //return new OkObjectResult($"OK");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ec = GetContactsByCustomerID(svc, requestBody);

            var mappings = GetMappings();
            var listResults = new List<ResponseBody_Item>();
            foreach (var reqCustomer in requestBody.customers)
            {
                var contact = GetContact(ec, reqCustomer.MasterCustomerID);
                var isFound = contact != null;
                var resultItem = new ResponseBody_Item
                {
                    Remarks = isFound ? reqCustomer.MasterCustomerID : $"{reqCustomer.MasterCustomerID}: customer does not exist.",
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
    private const string CrmFieldToSearch = "wkcda_mastercustomerid";

    private ColumnSet GetColumnSetFromMapping(string? entityAlias = null)
    {
        var mappings = GetMappings();
        var crmFields = mappings.Values.Select(o => o.ToLower().Trim())
                                       .Where(o => !string.IsNullOrEmpty(o))
                                       .Where(o => entityAlias != null ? o.StartsWith($"{entityAlias}.") : !o.Contains("."))
                                       .Select(o => entityAlias != null ? o.Substring(($"{entityAlias}.").Length) : o)
                                       .ToArray();

        return new ColumnSet(crmFields);
    }

    private Entity GetContact(EntityCollection ec, string customerID)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>(CrmFieldToSearch) == customerID);
    }

    private EntityCollection GetContactsByCustomerID(ServiceClient svc, RequestBody requestBody)
    {
        var qe = new QueryExpression(CrmEntityToSearch);
        qe.ColumnSet = GetColumnSetFromMapping();
        qe.Criteria.AddCondition(CrmFieldToSearch, ConditionOperator.In, GetMasterCustomerIDs(requestBody));

        {
            var le = qe.AddLink("wkcda_membershiptierhistory", "wkcda_paidmembershiptierhistory", "wkcda_membershiptierhistoryid", JoinOperator.LeftOuter);
            le.EntityAlias = "mth";
            le.Columns = GetColumnSetFromMapping(le.EntityAlias);
        }


        return svc.RetrieveMultiple(qe);
    }

    private object[] GetMasterCustomerIDs(RequestBody requestBody)
    {
        return requestBody.customers.Select(o => o.MasterCustomerID)
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
        public required string MasterCustomerID { get; set; }
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

    #endregion
}