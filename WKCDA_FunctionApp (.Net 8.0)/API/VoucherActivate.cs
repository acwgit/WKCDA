using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Text.Json;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using static WKCDA_FunctionApp__.Net_8._0_.API.VoucherValidate;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class VoucherActivate : WS_Base
    {
        public VoucherActivate(ILogger<VoucherActivate> logger) : base(logger) { }

        [Function("VoucherActivate")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/VoucherActivate")] HttpRequest req)
        {
            _logger.LogInformation("APIName: VoucherActivate");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.voucherInfos == null || !requestBody.voucherInfos.Any())
                    return new OkObjectResult(new VoucherValidateResponse
                    {
                        Remarks = "Invalid request body",
                        lstVoucherResult = new List<ResponseBody>(),
                        isSuccess = false
                    });

                var wrapper = new VoucherValidateResponse
                {
                    Remarks = null,
                    lstVoucherResult = new List<ResponseBody>(),
                    isSuccess = true
                };

                foreach (var voucher in requestBody.voucherInfos)
                {
                    var response = new ResponseBody
                    {
                        VoucherCode = voucher.VoucherCode,
                        VoucherSFID = voucher.VoucherSFID,
                        voucherInfo = new VoucherInfo
                        {
                            VoucherCode = voucher.VoucherCode,
                            VoucherSFID = voucher.VoucherSFID,
                            Gallery = voucher.Gallery,
                            NoOfFreeGuest = voucher.NoOfFreeGuest,
                        },
                        Success = false,
                        Remarks = null
                    };

                    try
                    {
                        #region Validation + CRM Retrieval
                        // Validation + CRM Retrieval
                        if (string.IsNullOrEmpty(voucher.VoucherCode))
                        {
                            response.Remarks = "Voucher Code is required.";
                            wrapper.lstVoucherResult.Add(response);
                            wrapper.isSuccess = false;
                            continue;
                        }

                        var qe = new QueryExpression("wkcda_membershipvoucher")
                        {
                            ColumnSet = new ColumnSet(
                                "wkcda_vouchercode",
                                "wkcda_startdate",
                                "wkcda_enddate",
                                "wkcda_membershipvoucherreferencename",
                                "wkcda_initialactivationdate",
                                "wkcda_usetype",
                                "wkcda_codestatus",
                                "wkcda_discount",
                                "wkcda_benefittype",
                                "wkcda_category",
                                "wkcda_latestactivationdate",
                                "wkcda_usedtimes",
                                "wkcda_membershiptierhistory"
                            )
                        };
                        qe.Criteria.AddCondition("wkcda_vouchercode", ConditionOperator.Equal, voucher.VoucherCode);

                        var voucherEntity = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();

                        if (voucherEntity == null)
                        {
                            response.Remarks = "Voucher Code Not found";
                            wrapper.lstVoucherResult.Add(response);
                            wrapper.isSuccess = false;
                            continue;
                        }

                        var codeStatus = voucherEntity.GetAttributeValue<string>("wkcda_codestatus");
                        var startDate = voucherEntity.GetAttributeValue<DateTime?>("wkcda_startdate");
                        var endDate = voucherEntity.GetAttributeValue<DateTime?>("wkcda_enddate");

                        if (!string.IsNullOrEmpty(codeStatus) && codeStatus.Equals("Terminated", StringComparison.OrdinalIgnoreCase))
                        {
                            response.Remarks = "Voucher Code Invalid (Terminated)";
                            wrapper.lstVoucherResult.Add(response);
                            continue;
                        }

                        if (!string.IsNullOrEmpty(codeStatus) && codeStatus.Equals("Consumed", StringComparison.OrdinalIgnoreCase))
                        {
                            response.Remarks = "Voucher Code Already Consumed";
                            wrapper.lstVoucherResult.Add(response);
                            continue;
                        }

                        if (endDate.HasValue && endDate.Value.Date < DateTime.UtcNow.Date)
                        {
                            response.Remarks = "Voucher Code Expired";
                            wrapper.lstVoucherResult.Add(response);
                            continue;
                        }

                        if (startDate.HasValue && startDate.Value.Date > DateTime.UtcNow.Date)
                        {
                            response.Remarks = "Voucher Code Not Start";
                            wrapper.lstVoucherResult.Add(response);
                            continue;
                        }

                        #endregion

                        #region Activate Voucher
                        // Only activate if status is not already activated
                        if (string.IsNullOrEmpty(codeStatus) || !codeStatus.Equals("Activated", StringComparison.OrdinalIgnoreCase))
                        {
                            var updateEntity = new Entity("wkcda_membershipvoucher")
                            {
                                Id = voucherEntity.Id
                            };
                            updateEntity["wkcda_codestatus"] = "Activated";
                            updateEntity["wkcda_latestactivationdate"] = DateTime.UtcNow;
                            svc.Update(updateEntity);
                        }
                        #endregion

                        #region Map CRM Fields -> Response
                        response.Success = true;
                        response.Remarks = null;
                        response.StartDate = startDate?.ToString("yyyy-MM-dd");
                        response.EndDate = endDate?.ToString("yyyy-MM-dd");
                        response.ReferenceNo = voucherEntity.GetAttributeValue<string>("wkcda_membershipvoucherreferencename");
                        response.ActivationDate = voucherEntity.GetAttributeValue<DateTime?>("wkcda_initialactivationdate")?.ToString("yyyy-MM-dd");
                        response.UseType = voucherEntity.GetAttributeValue<string>("wkcda_usetype");
                        response.VoucherName = voucherEntity.GetAttributeValue<string>("wkcda_vouchername");
                        response.CodeStatus = "Activated";
                        response.Discount = voucherEntity.GetAttributeValue<double>("wkcda_discount");
                        response.BenefitType = voucherEntity.GetAttributeValue<string>("wkcda_benefittype");
                        response.Category = voucherEntity.GetAttributeValue<string>("wkcda_category");
                        response.LatestActivationDate = DateTime.UtcNow.ToString("dd-MM-yy");
                        response.UsedTimes = voucherEntity.GetAttributeValue<double>("wkcda_usedtimes");

                        // Membership Tier History
                        var tierRef = voucherEntity.GetAttributeValue<EntityReference>("wkcda_membershiptierhistory");
                        if (tierRef != null)
                        {
                            var tierEntity = svc.Retrieve("wkcda_membershiptierhistory", tierRef.Id, new ColumnSet("wkcda_member", "wkcda_membershiptier", "wkcda_membershiphistoryname"));
                            response.MembershipTierHistoryId = tierEntity.GetAttributeValue<string>("wkcda_membershiphistoryname");

                            var contactRef = tierEntity.GetAttributeValue<EntityReference>("wkcda_member");
                            if (contactRef != null)
                            {
                                var contactEntity = svc.Retrieve("contact", contactRef.Id, new ColumnSet("wkcda_mastercustomerid", "emailaddress1"));
                                response.MemberEmail = contactEntity.GetAttributeValue<string>("emailaddress1");
                                response.MemberId = contactEntity.GetAttributeValue<string>("wkcda_mastercustomerid");
                            }

                            var membershipTierRef = tierEntity.GetAttributeValue<EntityReference>("wkcda_membershiptier");
                            if (membershipTierRef != null)
                            {
                                var membershipTierEntity = svc.Retrieve("wkcda_membershiptier", membershipTierRef.Id, new ColumnSet("wkcda_businessunit"));
                                if (membershipTierEntity.GetAttributeValue<OptionSetValue>("wkcda_businessunit") != null)
                                    response.BusinessUnit = membershipTierEntity.FormattedValues["wkcda_businessunit"];
                            }
                        }
                        #endregion

                        wrapper.lstVoucherResult.Add(response);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception($"unauthorized: {ex.Message}");
                        }
                        _logger.LogError(ex, "Error validating voucher");
                    }
                }

                return new OkObjectResult(wrapper);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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
                { "VoucherCode", "wkcda_vouchercode" },
                { "VoucherName", "wkcda_vouchername" },
                { "VoucherNameTC", "wkcda_vouchername_tc" },
                { "VoucherNameSC", "wkcda_vouchername_sc" },
                { "StartDate", "wkcda_startdate" },
                { "EndDate", "wkcda_enddate" },
                { "ReferenceNo", "wkcda_referenceno" },
                { "ActivationDate", "wkcda_activationdate" },
                { "UseType", "wkcda_usetype" },
                { "EligibleGallery", "wkcda_eligiblegallery" },
                { "CodeStatus", "wkcda_codestatus" },
                { "BusinessUnit", "wkcda_businessunit" },
                { "Discount", "wkcda_discount" },
                { "BenefitType", "wkcda_benefittype" },
                { "Category", "wkcda_category" },
                { "LatestActivationDate", "wkcda_latestactivationdate" },
                { "UsedTimes", "wkcda_usedtimes" }
            };
        }

        #region Request/Response Models
        public class VoucherValidateResponse
        {
            public string Remarks { get; set; }
            public List<ResponseBody> lstVoucherResult { get; set; } = new();
            public bool isSuccess { get; set; }
        }
        public class RequestBody
        {
            public required List<VoucherInfo> voucherInfos { get; set; }
        }
        public class VoucherInfo
        {
            public string VoucherSFID { get; set; }
            public string VoucherCode { get; set; }
            public string Gallery { get; set; }
            public int? NoOfFreeGuest { get; set; }
        }
        public class ResponseBody
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public string VoucherSFID { get; set; }
            public string VoucherCode { get; set; }
            public string VoucherName { get; set; }
            public VoucherInfo voucherInfo { get; set; }
            public string UseType { get; set; }
            public double UsedTimes { get; set; }
            public string StartDate { get; set; }
            public string ReferenceNo { get; set; }
            public string MembershipTierHistoryId { get; set; }
            public string MemberId { get; set; }
            public string MemberEmail { get; set; }
            public string LatestActivationDate { get; set; }
            public string EndDate { get; set; }
            public string EligibleGallery { get; set; }
            public double Discount { get; set; }
            public string CodeStatus { get; set; }
            public string Category { get; set; }
            public string BusinessUnit { get; set; }
            public string BenefitType { get; set; }
            public string ActivationDate { get; set; }
        }
        #endregion
    }
}
