using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Wino.Core.Http
{

    /// <summary>
    /// We need to generate HttpRequestMessage for batch requests, and sometimes we need to
    /// serialize content as json. However, some of the fields like 'ODataType' must be ignored
    /// in order PATCH requests to succeed. Therefore Microsoft account synchronizer uses
    /// special JsonSerializerSettings for ignoring some of the properties.
    /// </summary>
    public class MicrosoftJsonContractResolver : DefaultContractResolver
    {
        private readonly HashSet<string> ignoreProps = new HashSet<string>()
        {
            "ODataType"
        };

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);

            if (ignoreProps.Contains(property.PropertyName))
            {
                property.ShouldSerialize = _ => false;
            }

            return property;
        }
    }
}
