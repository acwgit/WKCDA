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

public class CreateOnlineDonationTransactionWS : WS_Base
{
    public CreateOnlineDonationTransactionWS(ILogger<CreateOnlineDonationTransactionWS> logger) : base(logger) { }

    [Function("CreateOnlineDonationTransactionWS")]
    public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/CreateOnlineDonationTransactionWS")] HttpRequest req)
    {
        _logger.LogInformation("APIName: CreateOnlineDonationTransactionWS");
        _logger.LogInformation("startTime: " + DateTime.Now);

        try
        {
            var svc = GetServiceClient(req);
            var requestBody = await req.ReadFromJsonAsync<RequestBody>();

            var ecCountry = GetCountry(svc);
            var ecOptInChannel = GetOptInChannel(svc);

            var emails = requestBody.donationList.Select(d => d.AccountEmail)?.Cast<object>()?.ToArray();
            var ecContacts = GetContactByEmail(svc, emails);

            //var ecCampaigns = GetCampaigns(svc, requestBody.donationList.Select(d => d.CampaignCode)?.Cast<object>()?.ToArray());

            var listResults = new List<ResponseBody_Item>();
            var ecGiftToCreate = new EntityCollection { EntityName = "wkcda_gifttransaction" };

            foreach (var donation in requestBody.donationList)
            {
                var resultItem = new ResponseBody_Item
                {
                    requestIndex = requestBody.donationList.IndexOf(donation),
                    Success = false
                };
                listResults.Add(resultItem);


                #region Validations
                if (string.IsNullOrEmpty(donation.AccountEmail))
                {
                    resultItem.Remarks = "Please provide customer ID or Email";
                    resultItem.DonationDetail = donation;
                    continue;
                }

                /*if (string.IsNullOrEmpty(donation.CampaignCode) || !ecCampaigns.Entities.Any(c => c.GetAttributeValue<string>("wkcda_campaigncode") == donation.CampaignCode))
                {
                    resultItem.Remarks = "Invalid or missing CampaignCode.";
                    continue;
                }*/
                #endregion

                try
                {
                    #region Handle Contact
                    Entity contact = ecContacts.Entities.FirstOrDefault(c => c.GetAttributeValue<string>("emailaddress1") == donation.AccountEmail);

                    if (contact == null)
                    {
                        contact = new Entity("contact");
                        contact["emailaddress1"] = donation.AccountEmail;
                        contact["firstname"] = donation.FirstName;
                        contact["lastname"] = donation.LastName;
                        contact["wkcda_salutation"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "contact", "wkcda_salutation", donation.Salutation, _logger) ?? 0);
                        contact["wkcda_firstnamechi"] = donation.FirstNameChi;
                        contact["wkcda_lastnamechi"] = donation.LastNameChi;
                        contact["mobilephone"] = donation.Mobile;
                        contact["wkcda_primaryaddressline1"] = donation.Address1;
                        contact["wkcda_primaryaddressline2"] = donation.Address2;
                        contact["wkcda_primaryaddressline3"] = donation.Address3;
                        contact["wkcda_billingaddresscountry"] = GetCountryRef(ecCountry, donation.AddressCountry);
                        contact["wkcda_preferredlanguage"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "contact", "wkcda_preferredlanguage", donation.PreferredLanguage, _logger) ?? 0);
                        contact["wkcda_westkowloonenewsletter"] = donation.WK_eNews;
                        contact["wkcda_optinchannel1"] = GetOptInChannel(ecOptInChannel, donation.OptInChannel1);
                        contact["wkcda_customersource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "contact", "wkcda_customersource", donation.CustomerSource, _logger) ?? 0);
                        contact["wkcda_fundraisinginterest"] = CRMEntityHelper.getMutiselectOptionValues(svc, "contact", "wkcda_fundraisinginterest", donation.FundraisingInterest);

                        contact.Id = svc.Create(contact);
                    }
                    contact = svc.Retrieve("contact", contact.Id, new ColumnSet(
                        "wkcda_salutation",
                        "firstname",
                        "lastname",
                        "wkcda_firstnamechi",
                        "wkcda_lastnamechi",
                        "wkcda_mastercustomerid"
                    ));
                    #endregion

                    #region Create Gift Transaction
                    Entity giftEntity = new Entity("wkcda_gifttransaction");
                    giftEntity["wkcda_giftid"] = donation.GiftID;
                    var salutationVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_salutation", donation.Salutation, _logger);
                    if (salutationVal.HasValue)
                        giftEntity["wkcda_salutation"] = new OptionSetValue(salutationVal.Value);
                    giftEntity["wkcda_firstname"] = contact.GetAttributeValue<string>("firstname");
                    giftEntity["wkcda_lastname"] = contact.GetAttributeValue<string>("lastname");
                    giftEntity["wkcda_firstname_chi"] = contact.GetAttributeValue<string>("wkcda_firstnamechi");
                    giftEntity["wkcda_lastname_chi"] = contact.GetAttributeValue<string>("wkcda_lastnamechi");
                    giftEntity["wkcda_member"] = contact.ToEntityReference();
                    giftEntity["wkcda_campaigncode"] = donation.CampaignCode;
                    giftEntity["wkcda_giftamount"] = donation.GiftAmount;
                    giftEntity["wkcda_remarks"] = donation.Remarks;
                    giftEntity["wkcda_giftdate"] = donation.GiftDate;
                    var donationSourceVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_donationsource", donation.DonationSource, _logger);
                    if (donationSourceVal.HasValue)
                    giftEntity["wkcda_donationsource"] = new OptionSetValue(donationSourceVal.Value);
                    var tributeTypeVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_tributetype", donation.TributeType, _logger);
                    if (tributeTypeVal.HasValue)
                    giftEntity["wkcda_tributetype"] = new OptionSetValue(tributeTypeVal.Value);
                    giftEntity["wkcda_tributename"] = donation.TributeName;
                    var giftTypeVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_gifttype", donation.GiftType, _logger);
                    if (giftTypeVal.HasValue)
                    giftEntity["wkcda_gifttype"] = new OptionSetValue(giftTypeVal.Value);
                    var giftCurrencyVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_paymentmethod", donation.PaymentMethod, _logger);
                    if (giftCurrencyVal.HasValue)
                    giftEntity["wkcda_paymentmethod"] = new OptionSetValue(giftCurrencyVal.Value);
                    var statusVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_status", donation.Status, _logger);
                    if (statusVal.HasValue)
                    giftEntity["wkcda_status"] = new OptionSetValue(statusVal.Value);
                    var legalEntityVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_legalentitybankaccount", donation.LegalEntity, _logger);
                    if (legalEntityVal.HasValue)
                    giftEntity["wkcda_appeal"] = new OptionSetValue(legalEntityVal.Value);
                    var appealVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_appeal", donation.Appeal, _logger);
                    if (appealVal.HasValue)
                    giftEntity["wkcda_appeal"] = new OptionSetValue(appealVal.Value);
                    var receiptRequiredVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_receiptrequired", donation.ReceiptRequired, _logger);
                    if (receiptRequiredVal.HasValue)
                    giftEntity["wkcda_receiptrequired"] = new OptionSetValue(receiptRequiredVal.Value);
                    var receiptHandlingVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_receipthandling", donation.ReceiptHandling, _logger);
                    if (receiptHandlingVal.HasValue)
                    giftEntity["wkcda_receipthandling"] = new OptionSetValue(receiptHandlingVal.Value);
                    giftEntity["wkcda_acknowledgementperiodyear"] = donation.AcknowledgementPeriod;
                    giftEntity["wkcda_supportinglink"] = donation.SupportingLink;
                    giftEntity["wkcda_chequenumber"] = donation.ChequeNumber;
                    giftEntity["wkcda_banktransferreferencenumber"] = donation.BankTransferReferenceNumber;
                    giftEntity["wkcda_paymenttransaction"] = donation.PaymentTransaction;
                    giftEntity["wkcda_transactionnumber"] = donation.TransactionNumber;
                    giftEntity["wkcda_retrylink"] = donation.RetryLink;
                    giftEntity["wkcda_retrydate"] = donation.RetryDate;
                    giftEntity["wkcda_paymentdate"] = donation.PaymentDate;
                    var paymentMethodVal = CRMEntityHelper.getOptionSetValue(svc, "wkcda_gifttransaction", "wkcda_paymentmethod", donation.PaymentMethod, _logger);
                    if (paymentMethodVal.HasValue)
                    giftEntity["wkcda_paymentmethod"] = new OptionSetValue(paymentMethodVal.Value);

                    var giftGuid = svc.Create(giftEntity);

                    var createdGift = svc.Retrieve("wkcda_gifttransaction", giftGuid, new ColumnSet("wkcda_giftid"));
                    // Retrieve the linked contact's master customer ID
                    var memberRef = giftEntity.GetAttributeValue<EntityReference>("wkcda_member") ?? contact.ToEntityReference();
                    if (memberRef != null)
                    {
                        var member = svc.Retrieve("contact", memberRef.Id, new ColumnSet("wkcda_mastercustomerid"));
                        // If master customer ID is empty, fallback to contact GUID
                        resultItem.CustomerId = member.GetAttributeValue<string>("wkcda_mastercustomerid") ?? member.Id.ToString();
                    }


                    resultItem.Success = true;
                    resultItem.GiftID = donation.GiftID;
                    // get mastercustomerid (text field) from the contact record
                    if (contact.Attributes.Contains("wkcda_mastercustomerid"))
                    {
                        resultItem.CustomerId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                    }
                    else
                    {
                        resultItem.CustomerId = null; // fallback if not found
                    }
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

                        resultItem.Success = false;
                        resultItem.Remarks = ex.Message;
                        resultItem.DonationDetail = donation;

                    }

                }

            }
            // Prepare conditional serialization for System.Text.Json
            foreach (var item in listResults)
            {
                if (item.Success)
                {
                    item.DonationDetail = null; // remove DonationDetail
                }
                else
                {
                    item.GiftID = null;
                    item.CustomerId = null;
                }
            }

            if (ecGiftToCreate.Entities.Count > 0)
            {
                var createMultipleRequest = new CreateMultipleRequest { Targets = ecGiftToCreate };
                svc.Execute(createMultipleRequest);
            }

            return new OkObjectResult(listResults);
        }
        catch (JsonException)
        {
            return new OkObjectResult(new ResponseBody_Item { Remarks = "Invalid JSON", Success = false });
        }
        catch (AuthenticationException ex)
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

    #region Mappings
    protected override Dictionary<string, string> GetMappings()
    {
        return new Dictionary<string, string>
        {
            // Contact fields
            { "AccountEmail"                    , "emailaddress1" },
            { "Salutation"                      , "wkcda_salutation" },
            { "FirstName"                       , "firstname" },
            { "FirstNameChi"                    , "wkcda_firstnamechi" },
            { "LastName"                        , "lastname" },
            { "LastNameChi"                     , "wkcda_lastnamechi" },
            { "Mobile"                          , "mobilephone" },
            { "Address1"                        , "wkcda_primaryaddressline1" },
            { "Address2"                        , "wkcda_primaryaddressline2" },
            { "Address3"                        , "wkcda_primaryaddressline3" },
            { "AddressCountry"                  , "wkcda_primaryaddresscountry" },
            { "WK_eNews"                        , "wkcda_westkowloonenewsletter" },
            { "PreferredLanguage"               , "wkcda_preferredlanguage" },
            { "OptInChannel1"                   , "wkcda_optinchannel1" },
            { "CustomerSource"                  , "wkcda_customersource" },
            { "FundraisingInterest"             , "wkcda_fundraisinginterest" },

            // Gift transaction fields
            { "CampaignCode"                    , "wkcda_campaigncode" },
            { "GiftAmount"                      , "wkcda_giftamount" },
            { "DonationSource"                  , "wkcda_donationsource" },
            { "TributeType"                     , "wkcda_tributetype" },
            { "TributeName"                     , "wkcda_tributename" },
            { "Remarks"                         , "wkcda_remarks" },
            { "GiftDate"                        , "wkcda_giftdate" },
            { "GiftType"                        , "wkcda_gifttype" },
            { "GiftCurrency"                    , "wkcda_paymentmethod" },
            { "Status"                          , "wkcda_status" },
            { "LegalEntity"                     , "wkcda_legalentitybankaccount" },
            { "Appeal"                          , "wkcda_appeal" },
            { "ReceiptRequired"                 , "wkcda_receiptrequired" },
            { "ReceiptHandling"                 , "wkcda_receipthandling" },
            { "AcknowledgementPeriod"           , "wkcda_acknowledgementperiodyear" },
            { "SupportingLink"                  , "wkcda_supportinglink" },
            { "GiftID"                          , "wkcda_giftid" },
            { "ChequeNumber"                    , "wkcda_chequenumber" },
            { "BankTransferReferenceNumber"     , "wkcda_banktransferreferencenumber" },
            { "PaymentTransaction"              , "wkcda_paymenttransaction" },
            { "TransactionNumber"               , "wkcda_transactionnumber" },
            { "RetryLink"                       , "wkcda_retrylink" },
            { "RetryDate"                       , "wkcda_retrydate" },
            { "PaymentDate"                     , "wkcda_paymentdate" },
            { "PaymentMethod"                   , "wkcda_paymentmethod" }
        };
    }
    #endregion

    #region Helpers
    private EntityCollection GetCampaigns(ServiceClient svc, object[] campaignCodes)
    {
        if (campaignCodes == null || campaignCodes.Length == 0) return new EntityCollection();
        var qe = new QueryExpression("campaign") { ColumnSet = new ColumnSet("wkcda_campaigncode") };
        qe.Criteria.AddCondition("wkcda_campaigncode", ConditionOperator.In, campaignCodes);
        return svc.RetrieveMultiple(qe);
    }

    private EntityCollection GetContactByEmail(ServiceClient svc, object[] emails)
    {
        if (emails == null || emails.Length == 0) return new EntityCollection();
        var qe = new QueryExpression("contact")
        {
            ColumnSet = new ColumnSet("contactid", "emailaddress1", "wkcda_mastercustomerid")
        };
        qe.Criteria.AddCondition("emailaddress1", ConditionOperator.In, emails);
        return svc.RetrieveMultiple(qe);
    }
    private EntityCollection GetCountry(ServiceClient svc)
    {
        var qe = new QueryExpression("wkcda_country");
        qe.ColumnSet.AddColumns("wkcda_countryname");
        return svc.RetrieveMultiple(qe);
    }

    private EntityCollection GetOptInChannel(ServiceClient svc)
    {
        var qe = new QueryExpression("wkcda_optinchannel");
        qe.ColumnSet.AddColumns("wkcda_channelname");
        return svc.RetrieveMultiple(qe);
    }

    private EntityReference GetCountryRef(EntityCollection ec, string name)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_countryname") == name)?.ToEntityReference();
    }

    private EntityReference GetOptInChannel(EntityCollection ec, string name)
    {
        return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_channelname") == name)?.ToEntityReference();
    }
    #endregion

    #region Request & Response Classes
    class RequestBody
    {
        public required List<DonationInput> donationList { get; set; }
    }

    class DonationInput
    {
        public string AccountEmail { get; set; }
        public string Salutation { get; set; }
        public string FirstName { get; set; }
        public string FirstNameChi { get; set; }
        public string LastName { get; set; }
        public string LastNameChi { get; set; }
        public string Mobile { get; set; }
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string Address3 { get; set; }
        public string AddressCountry { get; set; }
        public bool WK_eNews { get; set; }
        public string PreferredLanguage { get; set; }
        public string OptInChannel1 { get; set; }
        public string CustomerSource { get; set; }
        public string FundraisingInterest { get; set; }

        // Gift transaction fields
        public string CampaignCode { get; set; }
        public decimal GiftAmount { get; set; }
        public string DonationSource { get; set; }
        public string TributeType { get; set; }
        public string TributeName { get; set; }
        public string Remarks { get; set; }
        public DateTime GiftDate { get; set; }
        public string GiftType { get; set; }
        public string GiftCurrency { get; set; }
        public string Status { get; set; }
        public string LegalEntity { get; set; }
        public string Appeal { get; set; }
        public string ReceiptRequired { get; set; }
        public string ReceiptHandling { get; set; }
        public double AcknowledgementPeriod { get; set; }
        public string SupportingLink { get; set; }
        public string GiftID { get; set; }
        public string ChequeNumber { get; set; }
        public string BankTransferReferenceNumber { get; set; }
        public string PaymentTransaction { get; set; }
        public string TransactionNumber { get; set; }
        public string RetryLink { get; set; }
        public DateTime? RetryDate { get; set; }
        public DateTime? PaymentDate { get; set; }
        public string PaymentMethod { get; set; }
    }

    class ResponseBody_Item
    {
        public bool Success { get; set; }
        public string Remarks { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GiftID { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomerId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DonationInput? DonationDetail { get; set; }

        [JsonIgnore] public int requestIndex;
        [JsonIgnore] public int ecIndex;

        public void PrepareForSerialization()
        {
            if (Success)
            {
                // success → keep GiftID + CustomerId, remove DonationDetail
                DonationDetail = null;
            }
            else
            {
                // failure → keep DonationDetail, remove GiftID + CustomerId
                GiftID = null;
                CustomerId = null;
            }
        }
    }
    #endregion
}
