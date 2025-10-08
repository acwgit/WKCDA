using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API;

public class CreateCustomerWS : WS_Base
{
    public CreateCustomerWS(ILogger<CreateCustomerWS> logger) : base(logger)
    {
    }

    [Function("CreateCustomerWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/CreateCustomerWS")] HttpRequest req)
    {
        _logger.LogInformation("APIName: CreateCustomerWS");
        _logger.LogInformation("startTime: " + DateTime.Now);
        //return new OkObjectResult($"OK");
        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ecCountry = GetCountry(svc);
            var ecOptInChannel = GetOptInChannel(svc);

            var ecCustomerProfileSettings = GetCustomerProfileSetting(svc);

            var mappings = GetMappings();
            var listResults = new List<ResponseBody_Item>();

            var emailList = requestBody?.customerList?.Where(o => !string.IsNullOrEmpty(o.Email))
                                                      .Select(o => o.Email?.Trim());
            var ecContactForValidations = GetContactByEmail(svc, emailList?.Cast<object>()?.ToArray());

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
                    MasterCustomerID = null,
                };
                listResults.Add(resultItem);


                #region Validations
                {
                    var isValid = true;
                    if (string.IsNullOrEmpty(c.Email))
                    {
                        resultItem.Remarks = $"{c.LastName} {c.FirstName}:email is mandatory.";
                        isValid = false;
                        continue;
                    }
                    // check if first and last name contains Chinese characters
                    if (System.Text.RegularExpressions.Regex.IsMatch(c.FirstName ?? string.Empty, @"[\u4e00-\u9fff]") ||
                        System.Text.RegularExpressions.Regex.IsMatch(c.LastName ?? string.Empty, @"[\u4e00-\u9fff]"))
                    {
                        resultItem.Remarks = $"{c.LastName} {c.FirstName}: name should not contain Chinese characters.";
                        isValid = false;
                        continue;
                    }

                    var duplicatedContact = (Contact)ecContactForValidations.Entities.FirstOrDefault(o => ((Contact)o).EmailAddress1 == c.Email);
                    if (duplicatedContact != null)
                    {
                        resultItem.Remarks = $"{c.Email}:email is duplicated.";
                        resultItem.MasterCustomerID = duplicatedContact.WKCDA_MasterCustomerId;
                        isValid = false;
                        continue;
                    }
                    // validate MobilePhoneCountryCode lookup
                    if (!string.IsNullOrWhiteSpace(c.MobilePhoneCountryCode))
                    {
                        var qeCountry = new QueryExpression("wkcda_country")
                        {
                            ColumnSet = new ColumnSet("wkcda_countryid", "wkcda_countryname"),
                            Criteria =
                            {
                                Conditions =
                                {
                                    new ConditionExpression("wkcda_countryname", ConditionOperator.Equal, c.MobilePhoneCountryCode.Trim())
                                }
                            }
                        };

                        var countryRecord = svc.RetrieveMultiple(qeCountry).Entities.FirstOrDefault();

                        if (countryRecord == null)
                        {
                            resultItem.Remarks = $"{c.Email}: Mobile Phone Country Code '{c.MobilePhoneCountryCode}' does not exist in CRM.";
                            isValid = false;
                            continue;
                        }
                    }
                    //if (!isValid)
                    //    continue;
                }
                #endregion

                try
                {
                    #region Create Contact

                    Contact e = new Contact();
                    e.WKCDA_RecordType = Contact_WKCDA_RecordType.Member;
                    e.WKCDA_PrimaryAddressLine1 = c.Address1;
                    e.WKCDA_PrimaryAddressLine2 = c.Address2;
                    e.WKCDA_PrimaryAddressLine3 = c.Address3;
                    e.WKCDA_BillingAddressCountry = GetCountryRef(ecCountry, c.AddressCountry);
                    e.WKCDA_BillingAddressLine1 = c.BillingAddress1;
                    e.WKCDA_BillingAddressLine2 = c.BillingAddress2;
                    e.WKCDA_BillingAddressLine3 = c.BillingAddress3;
                    e.WKCDA_BillingAddressCountry = GetCountryRef(ecCountry, c.BillingAddressCountry);
                    e.WKCDA_DeliveryAddressLine1 = c.DeliveryAddress1;
                    e.WKCDA_DeliveryAddressLine2 = c.DeliveryAddress2;
                    e.WKCDA_DeliveryAddressLine3 = c.DeliveryAddress3;
                    e.WKCDA_DeliveryAddressCountry = GetCountryRef(ecCountry, c.DeliveryAddressCountry);
                    e.WKCDA_WestKoWLoOneNewsletter = c.WK_eNews;
                    e.WKCDA_MMagazine = c.MPlus_eNews;
                    e.WKCDA_MemberId = c.MemberId;
                    e.WKCDA_CompanyInput = c.Company;
                    e.Department = c.Department;
                    e.EmailAddress1 = c.Email;
                    e.Telephone2 = c.HomePhone;
                    e.MobilePhone = c.Mobile;
                    e.WKCDA_Title = c.Title;
                    e.WKCDA_LineId = c.LineID;
                    e.WKCDA_WeChatId = c.WeChatID;
                    if (!string.IsNullOrEmpty(c.DateOfBirth))
                        e.Birthdate = Convert.ToDateTime(c.DateOfBirth);
                    e.FirstName = c.FirstName;
                    e.WKCDA_FirstNameChi = c.FirstNameChi;
                    e.GenderCode = MappingHelper.MapOptionset<Contact_GenderCode>(c.Gender);
                    e.LastName = c.LastName;
                    e.WKCDA_LastNameChi = c.LastNameChi;
                    e.FamilyStatusCode = MappingHelper.MapOptionset<Contact_FamilyStatusCode>(c.MaritalStatus);
                    e.WKCDA_MasterCustomerId = c.MasterCustomerID;
                    e.WKCDA_Salutation = MappingHelper.MapOptionset<WKCDA_Salutation>(c.Salutation);
                    e.WKCDA_SalutationChi = c.SalutationChi;
                    e.WKCDA_TicketDeliveryMethod = MappingHelper.MapOptionset<WKCDA_TicketDeliveryMethod>(c.DeliveryMethod);
                    if (c.UnsubscribeAll != null)
                    {
                        bool temp = false;
                        switch (c.UnsubscribeAll?.ToLower()?.Trim())
                        {
                            case "check":
                                temp = true;
                                break;
                        }
                        //e.WKCDA_UnsubscribeAll = Convert.ToBoolean(c.UnsubscribeAll);
                        e.WKCDA_UnsubscribeAll = temp;
                    }

                    e.WKCDA_EmailOpTinDate1 = c.EmailOptinDate1 != null ? Convert.ToDateTime(c.EmailOptinDate1) : null; 
                    e.WKCDA_EmailOpTinDate2 = c.EmailOptinDate2 != null ? Convert.ToDateTime(c.EmailOptinDate2) : null;
                    e.WKCDA_EmailOptOutdate1 = c.EmailOptOutDate1 != null ? Convert.ToDateTime(c.EmailOptOutDate1) : null; 
                    e.WKCDA_EmailOptOutdate2 = c.EmailOptOutDate2 != null ? Convert.ToDateTime(c.EmailOptOutDate2) : null;
                    e.WKCDA_OpTinChannel1 = GetOptInChannel(ecOptInChannel, c.OptInChannel1);
                    e.WKCDA_OpTinChannel2 = GetOptInChannel(ecOptInChannel, c.OptInChannel2);
                    e.PreferredContactMethodCode = MappingHelper.MapOptionset<Contact_PreferredContactMethodCode>(c.PreferredContactMethod);
                    e.WKCDA_PreferredLanguage = MappingHelper.MapOptionset<Msdyn_LanguageCodes>(c.PreferredLanguage);
                    e.WKCDA_Status = MappingHelper.MapOptionset<WKCDA_RecordStatus>(c.Status);
                    e.WKCDA_TicketingPatronAccountNo = c.TicketingPatronAccountNo;
                    e.WKCDA_CustomerSource = MappingHelper.MapOptionset<WKCDA_CustomerSource>(c.CustomerSource);
                    e.WKCDA_ArtForm = MappingHelper.MapMultiOptionset<WKCDA_ArtForm>(c.ArtForm);
                    e.WKCDA_Interest = MappingHelper.MapMultiOptionset<WKCDA_Interest>(c.Interest);
                    e.WKCDA_CustomerInterestsElected = MappingHelper.MapMultiOptionset<WKCDA_CustomerInterestsElected>(c.CustomerInterestSelected);

                    if(!string.IsNullOrEmpty(c.Interest) || !string.IsNullOrEmpty(c.ArtForm))
                    {
                        List<String> oldInterests = new List<String>();
                        if (!string.IsNullOrEmpty(c.Interest))
                        {
                            //oldInterests = MappingHelper.MapMultiOptionset<WKCDA_Interest>(c.Interest);
                        }
                    }

                    e.WKCDA_WKMeHomePhoneCountryCode = GetCountryRef(ecCountry, c.wk_HomePhoneCountryCode);
                    e.WKCDA_WKMeHomePhoneNumber = c.wk_HomePhoneNumber;
                    e.WKCDA_WKMeOfficePhoneCountryCode = GetCountryRef(ecCountry, c.wk_OfficePhoneCountryCode);
                    e.WKCDA_WKMeOfficePhoneNumber = c.wk_OfficePhoneNumber;
                    e.WKCDA_WKMeMobilePhoneCountryCode = GetCountryRef(ecCountry, c.MobilePhoneCountryCode);
                    e.WKCDA_WKMeMobilePhoneNumber = c.MobilePhoneNumber;
                    e.WKCDA_YearOfBirth = c.YearOfBirth;
                    e.WKCDA_EmailOpTinDate3= c.EmailOptinDate3 != null ? Convert.ToDateTime(c.EmailOptinDate3) : null;
                    e.WKCDA_EmailOptOutdate3 = c.EmailOptOutDate3 != null ? Convert.ToDateTime(c.EmailOptOutDate3) : null;
                    e.WKCDA_EmailOpTinDate5 = c.OptInDate5!=null? Convert.ToDateTime(c.OptInDate5):null; 
                    e.WKCDA_EmailOpTinDate4 = c.EmailOptinDate4 != null ? Convert.ToDateTime(c.EmailOptinDate4) : null; 
                    e.WKCDA_EmailOptOutdate5  = c.OptOutDate5 != null ? Convert.ToDateTime(c.OptOutDate5) : null;
                    e.WKCDA_EmailOptOutdate4 = c.EmailOptOutDate4 != null ? Convert.ToDateTime(c.EmailOptOutDate4) : null;
                    e.WKCDA_OpTinChannel3 = GetOptInChannel(ecOptInChannel, c.OptInChannel3);
                    e.WKCDA_OpTinChannel4 = GetOptInChannel(ecOptInChannel, c.OptInChannel4);
                    e.WKCDA_OpTinChannel5 = GetOptInChannel(ecOptInChannel, c.OptInChannel5);
                    e.WKCDA_MonthOfBirth = c.MonthofBirth;
                    e.WKCDA_AlternateContactPersonFirstName=c.AltCntcFirstName;
                    e.WKCDA_AlternateContactPersonLastName = c.AltCntcLastName;
                    e.WKCDA_AlternateContactPersonalEmail = c.AltCntcEmail;
                    e.WKCDA_AlternateContactMobileCountryCode = GetCountryRef(ecCountry, c.AltCntcMobileCountryCode); 
                    e.WKCDA_AlternateContactMobile = c.AltCntcMobile;
                    e.WKCDA_IsSubsidiaryAccount = c.IsSubsidaryAccount;
                    e.wkcda_ExternalEntraIDObjectID = c.ExternalEntraIDObjectID;
                    e.wkcda_FirstLoginIndicator = c.FirstLoginIndicator;
                    
                    // Handle subscription info (MSHOP only)
                    if (c.SubscriptionInfos != null && c.SubscriptionInfos.Any())
                    {
                        foreach (var info in c.SubscriptionInfos)
                        {
                            var subscriptionDate = info.SubscriptionDate ?? DateTime.Today;
                            var isOptOut = info.IsOptOut ?? false;

                            // Only handle MSHOP subscription
                            if (info.SubscriptionType?.ToUpper() == "MSHOP")
                            {
                                // Assign IsOptOut to wkcda_mshop field
                                e.WKCDA_MShop = !isOptOut;

                                // Assign opt-in/out dates
                                if (isOptOut)
                                {
                                    e.WKCDA_EmailOptOutdate3 = subscriptionDate;
                                }
                                else
                                {
                                    e.WKCDA_EmailOpTinDate3 = subscriptionDate;

                                    // Assign OptInChannel3 if SubscriptionSource exists
                                    if (!string.IsNullOrEmpty(info.SubscriptionSource))
                                    {
                                        e.WKCDA_OpTinChannel3 = GetOptInChannel(ecOptInChannel, info.SubscriptionSource);
                                    }
                                }

                                break; // Only first MSHOP subscription considered
                            }
                        }
                    }

                    //e.WKCDA_MembershipSource = c.MembershipSource;

                    //var id = svc.Create(e);
                    //createMultipleRequest.Targets.Entities.Add(e);

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
                    resultItem.Remarks = $"{c.Email}";
                    _logger.LogInformation("Success : false , Remarks: " + $"{c.Email}");
                }
            }
            if (ecContactToCreate.Entities.Count > 0)
            {
                var createMultipleRequest = new CreateMultipleRequest
                {
                    Targets = ecContactToCreate
                };

                try
                {
                    var aaaa = (CreateMultipleResponse)svc.Execute(createMultipleRequest);
                    var bbb = aaaa.Ids.Cast<object>().ToArray();
                    var ecTemp = GetCreatedContact(svc, bbb);

                    for (int i = 0; i < aaaa.Ids.Length; i++)
                    {
                        var id = aaaa.Ids[i];
                        var e = ecTemp.Entities.FirstOrDefault(o => o.Id == id);
                        var masterCustomerId = e?.GetAttributeValue<string>("wkcda_mastercustomerid");
                        var email = e?.GetAttributeValue<string>("emailaddress1");
                        var item = listResults.FirstOrDefault(o => o.ecIndex == i);

                        if (item != null)
                        {
                            item.MasterCustomerID = masterCustomerId;
                            item.Success = id != Guid.Empty;
                        }
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogInformation("createMultipleRequest : "+ex.InnerException.ToString());
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
        //    return new ObjectResult(new
        //    {
        //        error = "Bad Request",
        //        message = ex.Message
        //    })
        //    {
        //        StatusCode = StatusCodes.Status400BadRequest
        //    };



        //}
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
                { "EmailOptinDate3"             , "wkcda_emailoptindate3" },
                { "EmailOptOutDate3"            , "wkcda_emailoptoutdate3" },
                { "EmailOptinDate4"             , "wkcda_emailoptindate4" },
                { "EmailOptOutDate4"            , "wkcda_emailoptoutdate4" },
                { "OptinDate5"                  , "wkcda_emailoptindate5" },
                { "OptOutDate5"                 , "wkcda_emailoptoutdate5" },
                { "OptInChannel1"               , "wkcda_optinchannel1" },
                { "OptInChannel2"               , "wkcda_optinchannel2" },
                { "OptInChannel4"               , "wkcda_optinchannel4" },
                { "OptInChannel5"               , "wkcda_optinchannel5" },
                { "MembershipSource"            , "wkcda_membershipsource" },
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
                { "AltCntcFirstName"            , "wkcda_alternatecontactpersonfirstname" },
                { "AltCntcLastName"             , "wkcda_alternatecontactpersonlastname" },
                { "AltCntcEmail"                , "wkcda_alternatecontactpersonalemail" },
                { "AltCntcMobileCountryCode"    , "wkcda_alternatecontactmobilecountrycode" },
                { "AltCntcMobile"               , "wkcda_alternatecontactmobile" },
                { "IsSubsidaryAccount"          , "wkcda_issubsidiaryaccount" },
                { "FirstLoginIndicator"         , "wkcda_firstloginindicator" },
                { "NeedEligibilityCheck"        , "wkcda_needeligibilitycheck" }
            };
    }

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

    private EntityCollection GetCountry(ServiceClient svc)
    {
        var qe = new QueryExpression("wkcda_country");
        qe.ColumnSet.AddColumns("wkcda_countryname");

        return svc.RetrieveMultiple(qe);
    }

    private EntityCollection GetContactByEmail(ServiceClient svc, object[] emails)
    {
        if (emails is null || emails.Length == 0)
            return new EntityCollection();

        var qe = new QueryExpression("contact");
        qe.ColumnSet.AddColumns("emailaddress1", "wkcda_mastercustomerid");
        qe.Criteria.AddCondition("emailaddress1", ConditionOperator.In, emails);

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
    public class SubscriptionInfo
    {
        public string? SubscriptionType { get; set; } // e.g., "MSHOP"
        public DateTime? SubscriptionDate { get; set; } // nullable
        public bool? IsOptOut { get; set; } // nullable
        public string? SubscriptionSource { get; set; }
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