using DataverseModel;
using Microsoft.Xrm.Sdk;

namespace WKCDA_FunctionApp__.Net_8._0_.Helper
{
    public static class MappingHelper
    {
        public static T MapOptionset<T>(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return ToEnum<T>(value);
            }
            return default(T);
        }

        public static List<T> MapMultiOptionset<T>(string value)
        {
            var result = new List<T>();
            if (!string.IsNullOrEmpty(value))
            {
                //var temp = value.Split(";").Select(o => (T)Enum.Parse(typeof(T), o));
                var temp = value.Split(";").Select(o => ToEnum<T>(o));

                result.AddRange(temp.ToList());
            }
            return result;
        }

        private static T ToEnum<T>(string str)
        {
            var enumType = typeof(T);
            foreach (var name in Enum.GetNames(enumType))
            {
                var enumMemberAttribute = ((OptionSetMetadataAttribute[])enumType.GetField(name).GetCustomAttributes(typeof(OptionSetMetadataAttribute), true)).Single();
                if (enumMemberAttribute.Name.Trim() == str.Trim())
                    return (T)Enum.Parse(enumType, name);
            }
            //throw exception or whatever handling you want or
            return default(T);
        }
    }
}