using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WKCDA_FunctionApp__.Net_8._0_.Helper
{
    public static class CRMEntityHelper
    {
        /// <summary>
        /// Get OptionSet integer value from a label.
        /// </summary>
        public static int? getOptionSetValue(IOrganizationService service, string entityName, string attributeName, string label, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityName,
                LogicalName = attributeName,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveAttributeResponse)service.Execute(request);
            var metadata = response.AttributeMetadata as PicklistAttributeMetadata;
            if (metadata == null)
            {
                logger.LogWarning($"Attribute '{attributeName}' is not a Picklist.");
                return null;
            }

            var option = metadata.OptionSet.Options
                .FirstOrDefault(o => string.Equals(o.Label?.UserLocalizedLabel?.Label?.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));

            if (option == null)
            {
                logger.LogWarning($"Label '{label}' not found in OptionSet '{attributeName}'.");
                return null;
            }
            logger.LogInformation($"OptionSet '{attributeName}' matched: Label='{option.Label?.UserLocalizedLabel?.Label}' Value={option.Value}");

            return option.Value;
        }
        /// <summary>
        /// Get MultiSelect OptionSet values from labels.
        /// </summary>
        public static OptionSetValueCollection? getMutiselectOptionValues(IOrganizationService service, string entityName, string attributeName, string labels)
        {
            if (string.IsNullOrWhiteSpace(labels))
                return null;

            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityName,
                LogicalName = attributeName,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveAttributeResponse)service.Execute(request);
            var metadata = response.AttributeMetadata as MultiSelectPicklistAttributeMetadata;

            var values = new OptionSetValueCollection();
            var inputLabels = labels.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(l => l.Trim());

            foreach (var inputLabel in inputLabels)
            {
                // Optional: log all options to check
                foreach (var opt in metadata.OptionSet.Options)
                {
                    var lbl = opt.Label?.UserLocalizedLabel?.Label?.Trim();
                    // Console.WriteLine($"OptionSet '{attributeName}' option: '{lbl}' = {opt.Value}");
                }

                var option = metadata.OptionSet.Options
                    .FirstOrDefault(o => string.Equals(o.Label?.UserLocalizedLabel?.Label?.Trim(), inputLabel, StringComparison.OrdinalIgnoreCase));

                if (option != null && option.Value.HasValue)
                    values.Add(new OptionSetValue(option.Value.Value));
            }

            return values.Count > 0 ? values : null;
        }

        /// <summary>
        /// Get Lookup EntityReference by matching a text value in a given attribute.
        /// </summary>
        public static EntityReference? getLookupEntityReference(IOrganizationService service, string targetEntity, string lookupAttribute, string lookupValue, string[] columns = null)
        {
            if (string.IsNullOrWhiteSpace(lookupValue))
                return null;

            var query = new QueryExpression(targetEntity)
            {
                ColumnSet = new ColumnSet(columns ?? new[] { lookupAttribute })
            };
            query.Criteria.AddCondition(lookupAttribute, ConditionOperator.Equal, lookupValue);

            var result = service.RetrieveMultiple(query).Entities.FirstOrDefault();
            if (result != null)
                return new EntityReference(targetEntity, result.Id);

            return null;
        }
        public static string? getOptionSetLabel(IOrganizationService service, string entityName, string attributeName, int value, ILogger logger)
        {
            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityName,
                LogicalName = attributeName,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveAttributeResponse)service.Execute(request);
            var metadata = response.AttributeMetadata as PicklistAttributeMetadata;
            if (metadata == null)
            {
                logger.LogWarning($"Attribute '{attributeName}' is not a Picklist.");
                return null;
            }

            var option = metadata.OptionSet.Options.FirstOrDefault(o => o.Value == value);
            if (option == null)
            {
                logger.LogWarning($"Value '{value}' not found in OptionSet '{attributeName}'.");
                return null;
            }

            return option.Label?.UserLocalizedLabel?.Label;
        }

    }
}