using System.Net;
using System.Security.Policy;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using static WKCDA_FunctionApp__.Net_8._0_.API.OnsiteAdmission;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class OnsiteAdmission : WS_Base
    {
        public OnsiteAdmission(ILogger<OnsiteAdmission> logger) : base(logger) { }
        ResponseBody_Item resultItem;
        [Function("OnsiteAdmission")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/OnsiteAdmission")] HttpRequest req)
        {
            _logger.LogInformation("APIName: OnsiteAdmission");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.onsiteAdmissions == null || !requestBody.onsiteAdmissions.Any())
                    return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid request body" });

                var response = new List<ResponseBody_Item>();
                
                // guest duplication logic
                var guestAdmissionList = new List<OnsiteAdmissionBean>();
                var ecAdmissionListCreate = new EntityCollection { EntityName = "wkcda_admissionandredemption" };

                foreach (var bn in requestBody.onsiteAdmissions)
                {
                    #region Validations
                    resultItem = new ResponseBody_Item
                    {
                        MemberID = bn.MemberID,
                        AdmissionID = null,
                        Success = false,
                        Remarks = null,
                        MemberTierHistoryId = bn.MembershipTierHistoryId
                    };
                    response.Add(resultItem);
                    if (bn.NoOfFreeGuest.HasValue && bn.NoOfFreeGuest > 1)
                    {


                        string bnJson = JsonSerializer.Serialize(bn);
                        bn.ExternalID += "_1";

                        for (int i = 2; i <= bn.NoOfFreeGuest; i++)
                        {
                            var bni = JsonSerializer.Deserialize<OnsiteAdmissionBean>(bnJson);
                            bni.ExternalID += $"_{i}";
                            guestAdmissionList.Add(bni);
                        }
                    }
                }

                        var voucherCodeSet = requestBody.onsiteAdmissions.Where(bn => !string.IsNullOrEmpty(bn.VoucherCode))
                                                                                .Select(bn => bn.VoucherCode)
                                                                                .ToArray();

                        var voucherMap = new Dictionary<string, EntityReference>();

                        var mthIdSet = requestBody.onsiteAdmissions.Where(bn => !string.IsNullOrEmpty(bn.MembershipTierHistoryId))
                                                                               .Select(bn => bn.MembershipTierHistoryId)
                                                                               .ToArray();

                        var mthidMap = new Dictionary<string, EntityReference>();


                #endregion

                try
                {


                    if (voucherCodeSet.Length > 0)
                    {
                        // TODO: replace with DB/API lookup
                        EntityCollection ec = GetMembershipVouchersByCode(svc, voucherCodeSet);
                        if (ec != null)
                        {
                            foreach (var item in ec.Entities)
                            {
                                voucherMap.Add(item.Attributes["wkcda_vouchercode"].ToString(), item.ToEntityReference());
                            }

                        }

                    }

                    if (mthIdSet.Length > 0)
                    {
                        // TODO: replace with DB/API lookup
                        EntityCollection ec = GetMTHbyName(svc, mthIdSet);
                        if (ec != null)
                        {
                            foreach (var item in ec.Entities)
                            {
                                mthidMap.Add(item.Attributes["wkcda_membershiphistoryname"].ToString(), item.ToEntityReference());
                            }

                        }

                    }
                    foreach (var item in guestAdmissionList)
                    {
                        EntityReference voucherRef;
                        EntityReference mthRef;
                        var admRec = GetAdm(svc,item,_logger);
                        admRec["wkcda_membershipvoucher"] = voucherMap.TryGetValue(item.VoucherCode, out voucherRef)?voucherRef:null;
                        admRec["wkcda_membershiptierhistory"] = mthidMap.TryGetValue(item.MembershipTierHistoryId, out mthRef)?mthRef:null;
                        ecAdmissionListCreate.Entities.Add(admRec);
                    }
                    if (ecAdmissionListCreate.Entities.Count > 0)
                    {
                        var createMultipleRequest = new CreateMultipleRequest { Targets = ecAdmissionListCreate };
                        svc.Execute(createMultipleRequest);
                        for (int i = 0; i < response.Count; i++)
                        {
                            response[i].Success = true;
                            response[i].Remarks = "Created Admission record";
                        }
                    }

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
                        _logger.LogError(ex, "Error creating Event Transaction");
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

                    

                    
                
                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" });
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
        private static Entity GetAdm(ServiceClient svc, OnsiteAdmissionBean bn, ILogger _logger)
        {
            Entity onsiteAdm = new Entity("wkcda_admissionandredemption");
            onsiteAdm["wkcda_admissionredemptiontime"] = bn.AdmissionTime;
            onsiteAdm["wkcda_admittedgallery"] = bn.AdmittedGallery;
            onsiteAdm["wkcda_businessunit"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_admissionandredemption", "wkcda_businessunit", bn.BusinessUnit,_logger) ?? 0); 
            //onsiteAdm["wkcda_membershiptierhistory"] = bn.MembershipTierHistoryId;
            onsiteAdm["wkcda_remarks"] = bn.Remarks;
            onsiteAdm["wkcda_externalid"] = bn.ExternalID;
            //onsiteAdm["wkcda_membershipvoucher"] = string.IsNullOrEmpty(bn.VoucherSFID) ? null : bn.VoucherSFID;
            onsiteAdm["wkcda_eligibilitycheckatadmission"] = bn.EligibilityCheckAtAdmission;

            return onsiteAdm;
        }

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "RegistrationDate", "wkcda_registrationdate" },
                { "Event", "wkcda_eventname" },
                { "PersonAccount", "wkcda_membername" }, // lookup
                { "Status", "wkcda_status" },
                { "Salutation", "wkcda_salutation" },
                { "RegistrantFirstName", "wkcda_registrantfirstnameeng" },
                { "RegistrantLastName", "wkcda_registrantlastnameeng" },
                { "Mobile", "wkcda_mobilephoneno" },
                { "ProgrammeVenue", "wkcda_programmevenue" },
                { "NoofParticipants", "wkcda_numberofparticipants" },
                { "NumofTickets", "wkcda_numberoftickets" },
                { "PaymentMethod", "wkcda_paymentmethod" },
                { "DiscountCode", "wkcda_discountcode" },
                { "Price", "wkcda_price" },
                { "PriceZone", "wkcda_pricezoneclasstier" },
                { "SeatRow", "wkcda_seatnorow" },
                { "SeatNo", "wkcda_seatnoseat" },
                { "SeatSection", "wkcda_seatnosection" },
                { "PurchasingMethod", "wkcda_purchasingmethod" },
                { "RegistrationSource", "wkcda_registrationsource" },
            };
        }

        private EntityReference? GetContactReference(ServiceClient svc, string custId, string email)
        {
            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            if (!string.IsNullOrEmpty(custId))
                qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, custId);
            else if (!string.IsNullOrEmpty(email))
                qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
            else
                return null;

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.FirstOrDefault() != null ? new EntityReference("contact", ec.Entities[0].Id) : null;
        }
        private EntityReference? GetVoucherReference(ServiceClient svc, string custId, string email)
        {
            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            if (!string.IsNullOrEmpty(custId))
                qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, custId);
            else if (!string.IsNullOrEmpty(email))
                qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
            else
                return null;

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.FirstOrDefault() != null ? new EntityReference("contact", ec.Entities[0].Id) : null;
        }

        private EntityCollection GetMembershipVouchersByCode(ServiceClient svc, string[] eventCode)
        {
            var qe = new QueryExpression("wkcda_membershipvoucher") { ColumnSet = new ColumnSet("wkcda_vouchercode", "wkcda_membershipvoucherid", "wkcda_membershipvoucherreferencename") };
            qe.Criteria.AddCondition("wkcda_vouchercode", ConditionOperator.In, eventCode);
            var e = svc.RetrieveMultiple(qe);
            return e;
        }
        private EntityCollection GetMTHbyName(ServiceClient svc, string[] names)
        {
            var qe = new QueryExpression("wkcda_membershiptierhistory") { ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_membershiphistoryname") };
            qe.Criteria.AddCondition("wkcda_membershiphistoryname", ConditionOperator.In, names);
            var e = svc.RetrieveMultiple(qe);
            return e;
        }

        #region Request/Response Classes
        public class RequestBody
        {
            public required List<OnsiteAdmissionBean> onsiteAdmissions { get; set; }
        }

        public class OnsiteAdmissionBean
        {
            public DateTime? AdmissionTime { get; set; }
            public string AdmittedGallery { get; set; }
            public string BusinessUnit { get; set; }
            public string MembershipTierHistoryId { get; set; }
            public string Remarks { get; set; }
            public string ExternalID { get; set; }
            public string VoucherCode { get; set; }
            public string VoucherSFID { get; set; }
            public int? NoOfFreeGuest { get; set; }
            public string MemberID { get; set; }
            public bool? EligibilityCheckAtAdmission { get; set; }
        }

        public class OnsiteAdmissions
        {
            public string Id { get; set; }
            public DateTime? AdmissionTime { get; set; }
            public string AdmittedGallery { get; set; }
            public string BusinessUnit { get; set; }
            public string MembershipTierHistoryId { get; set; }
            public string ExternalID { get; set; }
            public string Remarks { get; set; }
            public string MembershipVoucherId { get; set; }
            public string MemberId { get; set; }
            public bool EligibilityCheckAtAdmission { get; set; }
        }

        public class MembershipVoucher
        {
            public string Id { get; set; }
            public string VoucherCode { get; set; }
        }

        public class MembershipTierHistory
        {
            public string Id { get; set; }
            public string MemberId { get; set; }
            public string MemberCode { get; set; }
        }
        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MemberID { get; set; }
            public string AdmissionID { get; set; }
        }
        #endregion
    }
}