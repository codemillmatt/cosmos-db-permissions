using Microsoft.Azure.Documents.Spatial;
using Newtonsoft.Json;
using System;

namespace PermissionBroker
{
    public class LocationUpdate
    {
        [JsonProperty("position")]
        public Point Position { get; set; }
        [JsonProperty("id")]
        public string Id { get; set; }
        public string Country { get; set; }
        public string Town { get; set; }
        public string State { get; set; }
        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }
        public DateTimeOffset InsertTime { get; set; }
        public string Mood { get; set; }
        public bool IsPublicCheckin{get; set; }
    }    
}