using Microsoft.Xrm.Sdk;
using static WKCDA_FunctionApp__.Net_8._0_.API.CreateCustomerWS;

namespace WKCDA_FunctionApp__.Net_8._0_.Model
{
    public class Contact_Base
    {
        public Contact_Base() { }
        public Contact_Base(Entity contact, Dictionary<string, string> mappings)
        {
            foreach (var mapping in mappings)
            {
                var p = this[mapping.Key];
                if (p is bool)
                {
                    bool fieldValue = false;

                    if (mapping.Value.Contains("."))
                    {
                        AliasedValue temp;
                        contact.TryGetAttributeValue<AliasedValue>(mapping.Value, out temp);

                        if (temp?.Value is bool)
                            fieldValue = (bool)temp.Value;
                    }
                    else
                        contact.TryGetAttributeValue(mapping.Value, out fieldValue);

                    this[mapping.Key] = fieldValue;
                }
                else
                {
                    string formattedValue, fieldValue;
                    contact.FormattedValues.TryGetValue(mapping.Value, out formattedValue);
                    if (mapping.Value.Contains("."))
                    {
                        AliasedValue temp;
                        contact.TryGetAttributeValue<AliasedValue>(mapping.Value, out temp);

                        fieldValue = temp?.Value?.ToString();
                    }
                    else
                        contact.TryGetAttributeValue(mapping.Value, out fieldValue);

                    this[mapping.Key] = formattedValue ?? fieldValue;
                }
            }
        }

        public object this[string propertyName]
        {
            get
            {
                // probably faster without reflection:
                // like:  return Properties.Settings.Default.PropertyValues[propertyName] 
                // instead of the following
                Type myType = typeof(Contact_Base);
                var myPropInfo = myType.GetProperty(propertyName);
                return myPropInfo.GetValue(this, null);
            }
            set
            {
                Type myType = typeof(Contact_Base);
                var myPropInfo = myType.GetProperty(propertyName);
                myPropInfo?.SetValue(this, value, null);
            }
        }

        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string Address3 { get; set; }
        public string AddressCountry { get; set; }
        public string BillingAddress1 { get; set; }
        public string BillingAddress2 { get; set; }
        public string BillingAddress3 { get; set; }
        public string BillingAddressCountry { get; set; }
        public string Company { get; set; }
        public string CustomerInterestSelected { get; set; }
        public string CustomerSource { get; set; }
        public string DateOfBirth { get; set; }
        public string DeliveryAddress1 { get; set; }
        public string DeliveryAddress2 { get; set; }
        public string DeliveryAddress3 { get; set; }
        public string DeliveryAddressCountry { get; set; }
        public string DeliveryMethod { get; set; }
        public string Department { get; set; }
        public string Email { get; set; }
        public string EmailOptinDate1 { get; set; }
        public string EmailOptinDate2 { get; set; }
        public string EmailOptinDate3 { get; set; }
        public string EmailOptOutDate1 { get; set; }
        public string EmailOptOutDate2 { get; set; }
        public string EmailOptOutDate3 { get; set; }
        public string FirstName { get; set; }
        public string FirstNameChi { get; set; }
        public string Gender { get; set; }
        public string HomePhone { get; set; }
        public string LastName { get; set; }
        public string LastNameChi { get; set; }
        public string LineID { get; set; }
        public string MaritalStatus { get; set; }
        public string MasterCustomerID { get; set; }
        public string MemberId { get; set; }
        public string MobileCountryCode { get; set; }
        public string Mobile { get; set; }

        public bool MPlus_eNews { get; set; }
        public bool HKPM_eNews { get; set; }
       // public bool HKPM_eNews { get; set; }
        public bool Mplus_Membership_eNews { get; set; }
        
        public string OptInChannel1 { get; set; }
        public string OptInChannel2 { get; set; }
        public string PreferredContactMethod { get; set; }
        public string PreferredLanguage { get; set; }
        public string PrimaryContactEmail { get; set; }
        public string PrimaryContactFax { get; set; }
        public string PrimaryContactName { get; set; }
        public string PrimaryContactTelephone { get; set; }
        public string Salutation { get; set; }
        public string SalutationChi { get; set; }
        public string Status { get; set; }
        public string TicketingPatronAccountNo { get; set; }
        public string Title { get; set; }
        public string UnsubscribeAll { get; set; }
        public string WeChatID { get; set; }
        public bool WK_eNews { get; set; }
        public string YearOfBirth { get; set; }

        public string ArtForm { get; set; }
        public string Interest { get; set; }
        public string wk_HomePhoneCountryCode { get; set; }
        public string wk_HomePhoneNumber { get; set; }
        public string wk_OfficePhoneCountryCode { get; set; }
        public string wk_OfficePhoneNumber { get; set; }
        public string MobilePhoneCountryCode { get; set; }
        public string MobilePhoneNumber { get; set; }
        public string MonthofBirth { get; set; }
        public string MembershipTier { get; set; }
        public string GroupType { get; set; }
        public bool Updated { get; set; }
        public string MembershipStatus { get; set; }
        public string AltCntcFirstName { get; set; }
        public string AltCntcLastName { get; set; }
        public string AltCntcEmail { get; set; }
        public string AltCntcMobileCountryCode { get; set; }
        public string AltCntcMobile { get; set; }
        public string OptInDate5 { get; set; }
        public string OptOutDate5 { get; set; }
        public string EmailOptinDate4 { get; set; }
        public string EmailOptOutDate4 { get; set; }
        public string OptInChannel3 { get; set; }
        public string OptInChannel4 { get; set; }
        public string OptInChannel5 { get; set; }
        public string MembershipSource { get; set; }
        public bool IsSubsidaryAccount { get; set; }
        public bool IsOverrideName { get; set; }
        public string InterestStep { get; set; }
        public string ReferenceNo { get; set; }
        public string ExternalEntraIDObjectID { get; set; }
        //WKCDA_HkPmMembersHiPeNews
        //WKCDA_MMembersHiPeNews
        public bool FirstLoginIndicator { get; set; }
        
        public List<SubscriptionInfo>? SubscriptionInfos { get; set; }

    }
}
