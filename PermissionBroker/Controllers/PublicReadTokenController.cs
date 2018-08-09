using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using PermissionBroker;
using Microsoft.Azure.Documents.Linq;
using System.IO;

namespace PermissionBroker.Controllers
{
    [Route("api/[controller]")]
    public class PublicReadTokenController : Controller
    {
        private static DocumentClient _client = null;
        private static string databaseId = "CDALocations";
        private static string collectionId = "Location";
        private static string privateCollectionId = "PrivateLocation";
        private static string permissionId = "LocationPK"; // needs to be unique per user
        private static string hostURL ="https://awesomecontactz.documents.azure.com:443/";
        private static DateTime BeginningOfTime = new DateTime(2017, 1, 1);


        public static DocumentClient Client
        {
            get
            {
                if (_client == null)
                {
                    _client = new DocumentClient();
                }
                return _client;
            }
        }

        [HttpGet()]
        public async Task<IActionResult> GetPublicRead()
        {
            // Leave this as very straight forward
            try
            {
                string userId = "generalPublic";

                var permission = await GetPublicPermission(userId);

                string serializedToken = SerializePermission(permission);

                return Ok(serializedToken);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        //TODO: put in a new controller
        [HttpGet("api/publicandprivate")]
        public async Task<IActionResult> GetReadOnlyCollections()
        {
            string userId = "masoucou";

            // TODO: Some sort of checking to make sure a user id is being passed in via the bearer token


            var permissionList = await RetrieveReadPermissionsForUser(userId, new List<string>{collectionId, privateCollectionId});

            var serializedPermissionList = new List<string>();

            foreach (var item in permissionList)
            {
                serializedPermissionList.Add(SerializePermission(item));                        
            }

            return Ok(serializedPermissionList);
 
        }

        private async Task<List<Permission>> RetrieveReadPermissionsForUser(string userId, List<string> objectsToGet)
        {
            List<Permission> returnPermissions = new List<Permission>();
            List<string> generateNewPermissionsFor = new List<string>();

            // Check the cache
            foreach (var obj in objectsToGet)
            {
                var permission = await RetrieveCachedPermission(userId, obj);

                if (permission != null)
                {
                    returnPermissions.Add(permission);
                }
                else
                {
                    generateNewPermissionsFor.Add(obj);
                }
            }

            if (generateNewPermissionsFor.Count > 0)
            {
                returnPermissions.AddRange(await GetReadOnlyCollectionPermissions(userId, generateNewPermissionsFor));
            }

            return returnPermissions;
        }

        private async Task<Permission> RetrieveCachedPermission(string userId, string objectToGet)
        {
            CollectionPermissionToken permissionDocument = await Client.ReadDocumentAsync<CollectionPermissionToken>(
                    UriFactory.CreateDocumentUri(databaseId, collectionId, $"{userId}-{objectToGet}-permission")
            );

            if (permissionDocument == null) return null;

            int expires = permissionDocument.Expires;
            int fiveMinAgo = Convert.ToInt32(DateTime.UtcNow.AddMinutes(-5).Subtract(BeginningOfTime).TotalSeconds);

            if (expires > fiveMinAgo)
            {
                // deserialize the permission 
                string serializedPermission = permissionDocument.SerializedPermission;

                using (var memStream = new MemoryStream())
                {
                    using (var streamWriter = new StreamWriter(memStream))
                    {
                        streamWriter.Write(serializedPermission.ToString());
                        streamWriter.Flush();
                        memStream.Position = 0;

                        var permission = Permission.LoadFrom<Permission>(memStream);

                        streamWriter.Close();

                        return permission;
                    }
                }
            }
            return null;
        }

        private async Task CacheUserCollectionToken(CollectionPermissionToken token)
        {
            token.Id = $"{token.UserId}-{token.ResourceId}-permission";

            await Client.UpsertDocumentAsync(UriFactory.CreateDocumentCollectionUri(databaseId, collectionId), token);
        }

        private async Task<Permission> GetPublicPermission(string userId)
        {
            Permission publicCollectionPermission = new Permission();

            try
            {
                var collectionPermissionId = $"{userId}-publoc";
                publicCollectionPermission = await Client.ReadPermissionAsync(UriFactory.CreatePermissionUri(databaseId, userId, collectionPermissionId));
            }
            catch (DocumentClientException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // The permission was not found - either the user (and permission) doesn't exist or permission doesn't exist
                    await CreateUserIfNotExistAsync(userId);

                    var newPermission = new Permission
                    {
                        PermissionMode = PermissionMode.Read,
                        Id = $"{userId}-publoc",
                        ResourceLink = UriFactory.CreateDocumentCollectionUri(databaseId, collectionId).ToString()
                    };

                    publicCollectionPermission = await Client.CreatePermissionAsync(UriFactory.CreateUserUri(databaseId, userId), publicCollectionPermission);
                }
                else { throw ex; }
            }

            return publicCollectionPermission;
        }

        //TODO: maybe instead of for accepting a collection here, just take in one by one
        private async Task<List<Permission>> GetReadOnlyCollectionPermissions(string userId, List<string> collectionNames)
        {
            List<Permission> collectionPermissions = new List<Permission>();

            foreach (var collName in collectionNames)
            {
                Permission collPermission = new Permission();
                try
                {
                    // permission names are unique to the user
                    var collPermissionId = $"{userId}-{collName}";

                    // Read the permission from Cosmos (will throw a 404 if it doesn't exist)
                    collPermission = await Client.ReadPermissionAsync(UriFactory.CreatePermissionUri(databaseId, userId, collPermissionId));
                }
                catch (DocumentClientException dcx)
                {
                    if (dcx.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Permission doesn't exist. First make sure the user exists
                        await CreateUserIfNotExistAsync(userId);

                        // New up a permissions for that user
                        var newPermission = new Permission
                        {
                            PermissionMode = PermissionMode.Read,
                            Id = $"{userId}-{collName}",
                            ResourceLink = UriFactory.CreateDocumentCollectionUri(databaseId, collName).ToString()
                        };

                        collPermission = await Client.CreatePermissionAsync(UriFactory.CreateUserUri(databaseId, userId), newPermission);
                    }
                    else {throw dcx;}
                }    

                // Add a document of this permission to Cosmos for caching reasons (so we don't have to grab it if it hasn't expired)
                var expires = Convert.ToInt32(DateTime.UtcNow.Subtract(BeginningOfTime).TotalSeconds) + 3600;
                var permissionToken = new CollectionPermissionToken
                {
                    Expires = expires, 
                    UserId = userId,
                    ResourceId = collName,
                    SerializedPermission = SerializePermission(collPermission)
                };
                await CacheUserCollectionToken(permissionToken);

                collectionPermissions.Add(collPermission);
            }

            return collectionPermissions;
        }

        private string SerializePermission(Permission permission)
        {
            string serializedPermission = "";

            using (var memStream = new MemoryStream())
            { 
                permission.SaveTo(memStream);
                memStream.Position = 0;

                using (StreamReader sr = new StreamReader(memStream))
                {
                    serializedPermission = sr.ReadToEnd();
                }
            }

            return serializedPermission;
        }

        private async Task CreateUserIfNotExistAsync(string userId)
        {
            try
            {
                await Client.ReadUserAsync(UriFactory.CreateUserUri(databaseId, userId));
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    await Client.CreateUserAsync(UriFactory.CreateDatabaseUri(databaseId), new User { Id = userId });
                }
            }

        }


    }
    public class CollectionPermissionToken
    {
        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; }

        [JsonProperty(PropertyName = "expires")]
        public int Expires { get; set; }

        [JsonProperty(PropertyName = "userid")]
        public string UserId { get; set; }

        [JsonProperty(PropertyName="serializedPermission")]
        public string SerializedPermission{get;set;}

        [JsonProperty(PropertyName = "resourceId")]
        public string ResourceId { get; set; }
    }
}
