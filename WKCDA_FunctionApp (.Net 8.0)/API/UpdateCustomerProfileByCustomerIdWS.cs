using System.Security.Authentication;
using System.ServiceModel.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class UpdateCustomerProfileByCustomerIdWS : WS_Base
{
    public UpdateCustomerProfileByCustomerIdWS(ILogger<UpdateCustomerProfileByCustomerIdWS> logger) : base(logger)
    {
    }

    [Function("UpdateCustomerProfileByCustomerIdWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/UpdateCustomerProfileByCustomerIdWS")] HttpRequest req)
    {
        _logger.LogInformation("APIName: CreateCustomerWS");
        _logger.LogInformation("startTime: " + DateTime.Now);
        var listResults = new List<ResponseBody_Item>();
        //return new OkObjectResult($"OK");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ecCountry = GetCountry(svc);
            var ecOptInChannel = GetOptInChannel(svc);

            var ecCustomerProfileSettings = GetCustomerProfileSetting(svc);

            var mappings = GetMappings();
            

            var emailList = requestBody?.customerList?.Where(o => !string.IsNullOrEmpty(o.MasterCustomerID))
                                                      .Select(o => o.MasterCustomerID?.Trim());
            var ecContactForValidations = GetContactByCustomerID(svc, emailList?.Cast<object>()?.ToArray());


            var ecContactToCreate = new EntityCollection()
            {
                EntityName = "contact"
            };
            foreach (var c in requestBody.customerList)
            {
                var resultItem = new ResponseBody_Item
                {
                    requestIndex = requestBody.customerList.IndexOf(c),
                    Remarks = $"{c.Email}",
                    Success = false,
                    MasterCustomerID = c.MasterCustomerID,
                };
               

                Guid? contactId = Guid.Empty;
                Contact? duplicatedContact = null;
                #region Validations
                {
                    var isValid = true;
                    // check if master customer id is blank
                    if (string.IsNullOrWhiteSpace(c.MasterCustomerID))
                    {
                        resultItem.Remarks = $"{c.Email}: customer's MasterCustomerID cannot be blank.";
                        resultItem.Success = false;
                        listResults.Add(resultItem);
                        continue;
                    }
                    // check if master customer id exists in crm
                    duplicatedContact = (Contact)ecContactForValidations.Entities.FirstOrDefault(o => ((Contact)o).WKCDA_MasterCustomerId == c.MasterCustomerID);
                    if (duplicatedContact == null)
                    {
                        resultItem.Remarks = $"{c.Email}: Master Customer ID does not exist.";
                        resultItem.Success = false;
                        listResults.Add(resultItem);
                        continue;
                    }
                    else
                    {
                        contactId = duplicatedContact.ContactId;
                        resultItem.Success = true;
                        resultItem.Remarks = c.Email;
                    }
                    //if (!isValid)
                    //    continue;
                }
                #endregion

                try
                {
                    #region Update Contact

                    Contact e = new Contact();
                    bool orgWK = e.WKCDA_WestKoWLoOneNewsletter== null ?false:(bool)e.WKCDA_WestKoWLoOneNewsletter;
                    bool isPioneer =false;
                    //bool isPioneer = e.MExpiredDate__c != null && e.MExpiredDate__c >= DateTime.Today;
                    //e.WKCDA_MasterCustomerId = c.MasterCustomerID;
                    e.ContactId= contactId;
                    if (!string.IsNullOrEmpty(c.Address1))
                        e.WKCDA_PrimaryAddressLine1 = c.Address1;
                    if (!string.IsNullOrEmpty(c.Address2))
                        e.WKCDA_PrimaryAddressLine2 = c.Address2;
                    if (!string.IsNullOrEmpty(c.Address3))
                        e.WKCDA_PrimaryAddressLine3 = c.Address3;
                    if (!string.IsNullOrEmpty(c.AddressCountry))
                        e.WKCDA_PrimaryAddressCountry = GetCountryRef(ecCountry, c.AddressCountry);
                    if (!string.IsNullOrEmpty(c.BillingAddress1))
                        e.WKCDA_BillingAddressLine1 = c.BillingAddress1;
                    if (!string.IsNullOrEmpty(c.BillingAddress2))
                        e.WKCDA_BillingAddressLine2 = c.BillingAddress2;
                    if (!string.IsNullOrEmpty(c.BillingAddress3))
                        e.WKCDA_BillingAddressLine3 = c.BillingAddress3;
                    if (!string.IsNullOrEmpty(c.BillingAddressCountry))
                        e.WKCDA_BillingAddressCountry = GetCountryRef(ecCountry, c.BillingAddressCountry);
                    if (!string.IsNullOrEmpty(c.DeliveryAddress1))
                        e.WKCDA_DeliveryAddressLine1 = c.DeliveryAddress1;
                    if (!string.IsNullOrEmpty(c.DeliveryAddress2))
                        e.WKCDA_DeliveryAddressLine2 = c.DeliveryAddress2;
                    if (!string.IsNullOrEmpty(c.DeliveryAddress3))
                        e.WKCDA_DeliveryAddressLine3 = c.DeliveryAddress3;
                    if (!string.IsNullOrEmpty(c.DeliveryAddressCountry))
                        e.WKCDA_DeliveryAddressCountry = GetCountryRef(ecCountry, c.DeliveryAddressCountry);
                    if (c.WK_eNews == false || (IsInternal002(c.CustomerSource) && orgWK))
                    {
                        // keep existing
                    }
                    else
                    {
                        e.WKCDA_WestKoWLoOneNewsletter = c.WK_eNews;
                    }


                    e.WKCDA_MMagazine = c.MPlus_eNews; // == false ? e.WKCDA_MMagazine : c.MPlus_eNews;
                    e.WKCDA_MMembersHiPeNews = c.Mplus_Membership_eNews; //== false ? e.WKCDA_MMembersHiPeNews : c.Mplus_Membership_eNews;
                    e.WKCDA_HkPmMembersHiPeNews = c.HKPM_eNews; // == false ? e.WKCDA_HkPmMembersHiPeNews : c.WKCDA_HkPmMembersHiPeNews;
                    
                    //"WK_eNews": true,--
                    //"MPlus_eNews": true,--
                    //"HKPM_eNews": true,
                    //"Mplus_Membership_eNews": false,--

                    //e.WKCDA_MemberId = c.MemberId;
                    if (!string.IsNullOrEmpty(c.Company))
                        e.WKCDA_CompanyInput = c.Company;
                    if (!string.IsNullOrEmpty(c.Department))
                        e.Department = c.Department;
                    if (!string.IsNullOrEmpty(c.Email))
                        e.EmailAddress1 =  c.Email;
                    if (!string.IsNullOrEmpty(c.HomePhone))
                        e.Telephone2 = c.HomePhone;
                    if (!string.IsNullOrEmpty(c.Mobile))
                        e.MobilePhone = c.Mobile;
                    if (!string.IsNullOrEmpty(c.Title))
                        e.WKCDA_Title = c.Title;
                    if (!string.IsNullOrEmpty(c.LineID))
                        e.WKCDA_LineId = c.LineID;
                    if (!string.IsNullOrEmpty(c.WeChatID))
                        e.WKCDA_WeChatId = c.WeChatID;
                    if (!string.IsNullOrEmpty(c.DateOfBirth))
                        e.Birthdate = Convert.ToDateTime(c.DateOfBirth);
                    
                    if (!string.IsNullOrWhiteSpace(c.FirstName) && !(IsInternal002(c.CustomerSource) && isPioneer))
                    {
                        e.FirstName = c.FirstName;
                    }
                    if (!string.IsNullOrEmpty(c.FirstNameChi))
                        e.WKCDA_FirstNameChi =  c.FirstNameChi;
                    if (!string.IsNullOrEmpty(c.Gender))
                        e.GenderCode = MappingHelper.MapOptionset<Contact_GenderCode>(c.Gender);
                    if (!string.IsNullOrWhiteSpace(c.LastName) && !(IsInternal002(c.CustomerSource) && isPioneer))
                    {
                        e.LastName = c.LastName;
                    }
                    if (!string.IsNullOrEmpty(c.LastNameChi))
                        e.WKCDA_LastNameChi = c.LastNameChi;
                    if (!string.IsNullOrEmpty(c.DateOfBirth))
                        e.FamilyStatusCode = c.MaritalStatus==null? e.FamilyStatusCode:MappingHelper.MapOptionset<Contact_FamilyStatusCode>(c.MaritalStatus);
                    if (!string.IsNullOrWhiteSpace(c.Salutation) && !(c.CustomerSource == "onlineMember_Internal_002" && isPioneer))
                    {
                        e.WKCDA_Salutation = MappingHelper.MapOptionset<WKCDA_Salutation>(c.Salutation);
                    }
                    if (!string.IsNullOrEmpty(c.SalutationChi))
                        e.WKCDA_SalutationChi =  c.SalutationChi;
                    if (!string.IsNullOrEmpty(c.YearOfBirth))
                        e.WKCDA_YearOfBirth = c.YearOfBirth;

                    if (!string.IsNullOrEmpty(c.Interest) || !string.IsNullOrEmpty(c.ArtForm))
                    {
                        List<String> oldInterests = new List<String>();
                        if (!string.IsNullOrEmpty(c.Interest))
                        {
                            //oldInterests = MappingHelper.MapMultiOptionset<WKCDA_Interest>(c.Interest);
                        }
                    }
                    if (!string.IsNullOrEmpty(c.wk_HomePhoneCountryCode))
                        e.WKCDA_WKMeHomePhoneCountryCode = GetCountryRef(ecCountry, c.wk_HomePhoneCountryCode);
                    if (!string.IsNullOrEmpty(c.wk_HomePhoneNumber))
                        e.WKCDA_WKMeHomePhoneNumber = c.wk_HomePhoneNumber;
                    if (!string.IsNullOrEmpty(c.wk_OfficePhoneCountryCode))
                        e.WKCDA_WKMeOfficePhoneCountryCode = GetCountryRef(ecCountry, c.wk_OfficePhoneCountryCode);
                    if (!string.IsNullOrEmpty(c.wk_OfficePhoneNumber))
                        e.WKCDA_WKMeOfficePhoneNumber = c.wk_OfficePhoneNumber;
                    if (!string.IsNullOrEmpty(c.MobilePhoneCountryCode))
                        e.WKCDA_WKMeMobilePhoneCountryCode = GetCountryRef(ecCountry, c.MobilePhoneCountryCode);
                    if (!string.IsNullOrEmpty(c.MobilePhoneNumber))
                        e.WKCDA_WKMeMobilePhoneNumber = c.MobilePhoneNumber;
                    if (!string.IsNullOrWhiteSpace(c.Status))
                        e.WKCDA_Status = MappingHelper.MapOptionset<WKCDA_RecordStatus>(c.Status);
                    if (!string.IsNullOrWhiteSpace(c.ExternalEntraIDObjectID))
                        e.wkcda_ExternalEntraIDObjectID = c.ExternalEntraIDObjectID;
                    if (!string.IsNullOrWhiteSpace(c.EmailOptinDate1))
                        e.WKCDA_EmailOpTinDate1 = Convert.ToDateTime(c.EmailOptinDate1);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptinDate2))
                        e.WKCDA_EmailOpTinDate2 = Convert.ToDateTime(c.EmailOptinDate2);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptinDate3))
                        e.WKCDA_EmailOpTinDate3 = Convert.ToDateTime(c.EmailOptinDate3);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptinDate4))
                        e.WKCDA_EmailOpTinDate4 = Convert.ToDateTime(c.EmailOptinDate4);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptOutDate1))
                        e.WKCDA_EmailOptOutdate1 = Convert.ToDateTime(c.EmailOptOutDate1);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptOutDate2))
                        e.WKCDA_EmailOptOutdate2 = Convert.ToDateTime(c.EmailOptOutDate2);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptOutDate3))
                        e.WKCDA_EmailOptOutdate3 = Convert.ToDateTime(c.EmailOptOutDate3);
                    if (!string.IsNullOrWhiteSpace(c.EmailOptOutDate4))
                        e.WKCDA_EmailOptOutdate4 = Convert.ToDateTime(c.EmailOptOutDate4);
                    if (!string.IsNullOrWhiteSpace(c.OptInChannel1))
                        e.WKCDA_OpTinChannel1 = GetOptInChannel(ecOptInChannel, c.OptInChannel1);
                    if (!string.IsNullOrWhiteSpace(c.OptInChannel3))
                        e.WKCDA_OpTinChannel2 = GetOptInChannel(ecOptInChannel, c.OptInChannel2);
                    if (!string.IsNullOrWhiteSpace(c.OptInChannel3))
                        e.WKCDA_OpTinChannel3 = GetOptInChannel(ecOptInChannel, c.OptInChannel3);
                    if (!string.IsNullOrWhiteSpace(c.OptInChannel4))
                        e.WKCDA_OpTinChannel4 = GetOptInChannel(ecOptInChannel, c.OptInChannel4);
                    if (!string.IsNullOrWhiteSpace(c.OptInChannel5))
                        e.WKCDA_OpTinChannel5 = GetOptInChannel(ecOptInChannel, c.OptInChannel5);
                    e.wkcda_FirstLoginIndicator = c.FirstLoginIndicator;

                    resultItem.ecIndex = ecContactToCreate.Entities.Count;
                    ecContactToCreate.Entities.Add(e);


                    #endregion

                    //resultItem.MasterCustomerID = $"GUID:{id}";
                    //resultItem.Remarks = e.EmailAddress1;
                    //resultItem.Success = true;


                }
                catch (Exception)
                {
                    resultItem.Success = false;
                    resultItem.Remarks = $"{c.MasterCustomerID}";
                    _logger.LogInformation("Success : false , Remarks: " + $"{c.MasterCustomerID}");
                }
                listResults.Add(resultItem);
            }
            if (ecContactToCreate.Entities.Count > 0)
            {
                var createMultipleRequest = new UpdateMultipleRequest
                {
                    Targets = ecContactToCreate,
                     
                };

                try
                {
                    var aaaa = (UpdateMultipleResponse)svc.Execute(createMultipleRequest);
                    //var bbb = aaaa.Ids.Cast<object>().ToArray();
                    //var ecTemp = GetCreatedContact(svc, bbb);

                    for (int i = 0; i < ecContactToCreate.Entities.Count; i++)
                    {
                        var item = listResults.FirstOrDefault(o => o.ecIndex == i);
                        if (item != null)
                        {
                            item.Success = true;
                        }
                        /*
                        var id = aaaa.Results.Ids[i];
                        var e = ecTemp.Entities.FirstOrDefault(o => o.Id == id);
                        var masterCustomerId = e?.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var email = e?.GetAttributeValue<string>("emailaddress1");
                       

                       */
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogInformation("updateMultipleRequest : " + ex.InnerException.ToString());
                    throw;
                }

                
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
        //catch (MessageSecurityException ex)
        //{
        //    var resultItem = new ResponseBody_Item
        //    {
        //        Remarks = ex.Message,
        //        Success = false
        //    };
        //    listResults.Add(resultItem);
        //    return new OkObjectResult(listResults);
        //}
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
    

    // helper for "isInternal002"
    private static bool IsInternal002(string source)
    {
        return source == "wifi_Internal_002" || source == "onlineMember_Internal_002";
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
                { "Address1"                    , "wkcda_primaryaddressline1" },
                { "Address2"                    , "wkcda_primaryaddressline2" },
                { "Address3"                    , "wkcda_primaryaddressline3" },
                { "AddressCountry"              , "wkcda_primaryaddresscountry" },
                { "BillingAddress1"             , "wkcda_billingaddressline1" },
                { "BillingAddress2"             , "wkcda_billingaddressline2" },
                { "BillingAddress3"             , "wkcda_billingaddressline3" },
                { "BillingAddressCountry"       , "wkcda_billingaddresscountry" },
                { "DeliveryAddress1"            , "wkcda_deliveryaddressline1" },
                { "DeliveryAddress2"            , "wkcda_deliveryaddressline2" },
                { "DeliveryAddress3"            , "wkcda_deliveryaddressline3" },
                { "DeliveryAddressCountry"      , "wkcda_deliveryaddresscountry" },
                { "WK_eNews"                    , "wkcda_westkowloonenewsletter" },
                { "MPlus_eNews"                 , "wkcda_mmagazine" },
                { "MemberId"                    , "wkcda_memberid" },
                { "Company"                     , "wkcda_companyinput" },
                { "Department"                  , "department" },
                { "Email"                       , "emailaddress1" },
                { "HomePhone"                   , "telephone2" },
                { "Mobile"                      , "mobilephone" },
                { "Title"                       , "wkcda_title" },
                { "LineID"                      , "wkcda_lineid" },
                { "WeChatID"                    , "wkcda_wechatid" },
                { "DateOfBirth"                 , "birthdate" },
                { "FirstName"                   , "firstname" },
                { "FirstNameChi"                , "wkcda_firstnamechi" },
                { "Gender"                      , "gendercode" },
                { "LastName"                    , "lastname" },
                { "LastNameChi"                 , "wkcda_lastnamechi" },
                { "MaritalStatus"               , "familystatuscode" },
                { "MasterCustomerID"            , "wkcda_mastercustomerid" },
                { "Salutation"                  , "wkcda_salutation" },
                { "SalutationChi"               , "wkcda_salutationchi" },
                { "YearOfBirth"                 , "wkcda_yearofbirth" },
                { "CustomerInterestSelected"    , "wkcda_customerinterestselected" },
                { "DeliveryMethod"              , "wkcda_ticketdeliverymethod" },
                { "UnsubscribeAll"              , "wkcda_unsubscribeall" },
                { "EmailOptinDate1"             , "wkcda_emailoptindate1" },
                { "EmailOptinDate2"             , "wkcda_emailoptindate2" },
                { "EmailOptOutDate1"            , "wkcda_emailoptoutdate1" },
                { "EmailOptOutDate2"            , "wkcda_emailoptoutdate2" },
                { "OptInChannel1"               , "wkcda_optinchannel1" },
                { "OptInChannel2"               , "wkcda_optinchannel2" },
                { "PreferredContactMethod"      , "preferredcontactmethodcode" },
                { "PreferredLanguage"           , "wkcda_preferredlanguage" },
                { "Status"                      , "wkcda_status" },
                { "TicketingPatronAccountNo"    , "wkcda_ticketingpatronaccountno" },
                { "CustomerSource"              , "wkcda_customersource" },
                { "ArtForm"                     , "wkcda_artform" },
                { "Interest"                    , "wkcda_interest" },
                { "wk_HomePhoneCountryCode"     , "wkcda_wkmehomephonecountrycode" },
                { "wk_HomePhoneNumber"          , "wkcda_wkmehomephonenumber" },
                { "wk_OfficePhoneCountryCode"   , "wkcda_wkmeofficephonecountrycode" },
                { "wk_OfficePhoneNumber"        , "wkcda_wkmeofficephonenumber" },
                { "MobilePhoneCountryCode"      , "wkcda_wkmemobilephonecountrycode" },
                { "MobilePhoneNumber"           , "wkcda_wkmemobilephonenumber" },
                { "MonthofBirth"                , "wkcda_monthofbirth" },
                { "MembershipTier"              , "mth.wkcda_membershiphistoryname" },
                { "GroupType"                   , "mth.wkcda_grouptype" },
                { "Updated"                     , "mth.wkcda_updated" },
                { "MembershipStatus"            , "mth.wkcda_membershipstatus" },
                { "AltCntcFirstName"            , "wkcda_alternatecontactpersonfirstname" },
                { "AltCntcLastName"             , "wkcda_alternatecontactpersonlastname" },
                { "AltCntcEmail"                , "wkcda_alternatecontactpersonalemail" },
                { "AltCntcMobileCountryCode"    , "wkcda_alternatecontactmobilecountrycode" },
                { "AltCntcMobile"               , "wkcda_alternatecontactmobile" },
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
        return requestBody.customerList.Select(o => o.MasterCustomerID)
                                    .Distinct()
                                    .Cast<object>()
                                    .ToArray();
    }

    private EntityCollection GetCountry(ServiceClient svc)
    {
        var qe = new QueryExpression("wkcda_country");
        qe.ColumnSet.AddColumns("wkcda_countryname");

        return svc.RetrieveMultiple(qe);
    }

    private EntityCollection GetContactByCustomerID(ServiceClient svc, object[] emails)
    {
        if (emails is null || emails.Length == 0)
            return new EntityCollection();

        var qe = new QueryExpression("contact");
        qe.ColumnSet.AddColumns("emailaddress1", "wkcda_mastercustomerid");
        qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.In, emails);

        return svc.RetrieveMultiple(qe);
    }
    private EntityCollection GetCustomerProfileSetting(ServiceClient svc)
    {
        object[] devNames = ["Interest", "Art_Form"];

        var qe = new QueryExpression("wkcda_customerportalsetting");
        qe.ColumnSet.AddColumns("wkcda_sfdcid", "wkcda_customiseprofilesettingname", "wkcda_customerinterest", "wkcda_recordtypeid");
        //qe.Criteria.AddCondition("wkcda_recordtypeid", ConditionOperator.In, devNames);

        return svc.RetrieveMultiple(qe);
    }

    private EntityCollection GetCreatedContact(ServiceClient svc, object[] ids)
    {
        var qe = new QueryExpression("contact");
        qe.ColumnSet.AddColumns("emailaddress1", "wkcda_mastercustomerid");
        qe.Criteria.AddCondition("contactid", ConditionOperator.In, ids);

        return svc.RetrieveMultiple(qe);
    }

    private EntityReference GetCountryRef(EntityCollection ec, string name)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_countryname") == name)?.ToEntityReference();
    }

    private EntityCollection GetOptInChannel(ServiceClient svc)
    {
        var qe = new QueryExpression("wkcda_optinchannel");
        qe.ColumnSet.AddColumns("wkcda_channelname");

        return svc.RetrieveMultiple(qe);
    }

    private EntityReference GetOptInChannel(EntityCollection ec, string name)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_channelname") == name)?.ToEntityReference();
    }

    #region Request Class

    class RequestBody
    {
        public required List<Contact_Base> customerList { get; set; }
    }

    //class RequestContact : Contact_Base
    //{
    //    public RequestContact(Entity contact, Dictionary<string, string> mappings) : base(contact, mappings)
    //    {
    //    }

    //    public required bool WK_eNews { get; set; }
    //}

    #endregion

    #region Response Class

    class ResponseBody_Item
    {
        public bool Success { get; set; }
        public string? Remarks { get; set; }
        public string? MasterCustomerID { get; set; }

        [JsonIgnore]
        public int requestIndex;
        [JsonIgnore]
        public int ecIndex;
    }

    #endregion
}