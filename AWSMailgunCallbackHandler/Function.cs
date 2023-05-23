using System;
using Amazon.Lambda.Core;
using ServiceStack.Redis;
using System.Collections.Generic;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Linq;
using Newtonsoft.Json.Linq;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWSMailgunCallbackHandler
{
    public class Function
    {
        public const string Status = "Status";
        public const string RedisClientConnection = "unifiedmessagingrds.pbflrl.ng.0001.use1.cache.amazonaws.com";
        //public const string RedisClientConnection = "localhost";
        //public const int RedishStatusDB = 2;
        public const int RedishBatchStatusDB = 4;
        public const int RedishClickMapDB = 9;

        public dynamic UpdateEmailStatus(dynamic request, ILambdaContext context)
        {
            var headerValue = new Dictionary<string, string>();
            headerValue.Add("Access-Control-Allow-Headers", "*");
            headerValue.Add("Access-Control-Allow-Origin", "*");
            headerValue.Add("Access-Control-Allow-Methods", "*");
            try
            {
                LambdaLogger.Log("Mailgun callback called : "+ request.ToString());
                if (((Newtonsoft.Json.Linq.JContainer)request).HasValues)
                {
                    Dictionary<string, string> FormData = new Dictionary<string, string>();
                    string body = request.body;

                    string headerContentType = request.headers["content-type"];
                    if (!string.IsNullOrEmpty(headerContentType) && headerContentType.Contains("multipart/form-data; boundary="))
                    {
                        var Boundry = headerContentType.Replace("multipart/form-data; boundary=", "");
                        var errorBodyParas = body.Replace("\r\n\r\n", "###").Replace("\r\n", "").Replace("--", "").Replace("Content-Disposition: form-data;", "").Replace("mailto:", "").Replace("\"", "").Replace($"name=", "").Split($"{Boundry}").Where(x => x.Length > 0).Select(x => x.Trim()).ToList();
                        FormData = errorBodyParas.ToDictionary(s => s.Split("###")[0], s => s.Split("###")[1]);
                    }
                    else if (!string.IsNullOrEmpty(body))
                    {
                        body = System.Web.HttpUtility.UrlDecode(body);
                        if (ValidateJSON(body))
                        {
                            var node = JObject.Parse(body);
                            ConvertNestedJsonToSimpleJson(node, FormData);
                        }
                        else
                        {
                            var bodyarray = body.Split('&');
                            foreach (string value in bodyarray)
                            {
                                var keys = value.Split("=", 2);
                                try
                                {
                                    FormData.Add(keys[0], keys[1]);
                                }
                                catch
                                { }
                            }
                        }
                    }
                    if (FormData.Count > 0)
                    {
                        FormData.TryGetValue("MessageID", out string MessageId);

                        if(string.IsNullOrEmpty(MessageId))
                            throw new ApplicationException("MessageId Not found");
                        //if(!int.TryParse(MessageId, out int n))
                            //throw new ApplicationException("MessageId Incorrect");
                        FormData.TryGetValue("Message-Id", out string smsSid);
                        if (string.IsNullOrEmpty(smsSid))
                            FormData.TryGetValue("message-id", out smsSid);
                        FormData.TryGetValue("event", out string messageStatus);
                        FormData.TryGetValue("recipient", out string number);
                        List<string> ErrorStatus = "failed,undelivered,bounced,dropped".Split(',').ToList();
                        int ContactId = 0;
                        try
                        {
                            FormData.TryGetValue("RecipientId", out string RecipientId);
                            ContactId = int.Parse(RecipientId);
                        }
                        catch { }
                        if (ErrorStatus.Contains(messageStatus.ToString().Trim()))
                        {
                            messageStatus = FirstCharToUpper(messageStatus.ToString().Trim());
                            AddMessagestatusLogs(MessageId.ToString().Trim(), smsSid.ToString().Trim(), false, messageStatus, number.ToString().Trim());
                        }
                        else if (messageStatus.ToString().Trim() == "opened")
                        {
                            AddMessagestatusLogs(MessageId.ToString().Trim(), smsSid.ToString().Trim(), true, messageStatus, number.ToString().Trim(), isOpened: true, clicks: 1, ContactId: ContactId);
                        }
                        else if (messageStatus.ToString().Trim() == "clicked")
                        {
                            AddMessagestatusLogs(MessageId.ToString().Trim(), smsSid.ToString().Trim(), true, messageStatus, number.ToString().Trim(), clicks : 1, ContactId: ContactId);
                            try
                            {
                                FormData.TryGetValue("url", out string url);
                                if (!string.IsNullOrEmpty(url) && ContactId > 0 && !url.ToString().Contains("tinyurl") && !url.ToString().Contains("zpclk"))
                                {
                                    if (int.TryParse(MessageId.Split("_")[0], out var sId))
                                        AddClickMapLogs(new ClickMap { MessageDataId = sId, ContactId = ContactId, URL = url, Clicks = 1 });
                                }
                            }
                            catch
                            { }
                        }
                        else
                        {
                            AddMessagestatusLogs(MessageId.ToString().Trim(), smsSid.ToString().Trim(), true, messageStatus, number.ToString().Trim());
                        }
                    }
                    else
                    {
                       throw new ApplicationException("Empty body");
                    }
                    return new LambdaResponse
                    {
                        isBase64Encoded = false,
                        body = JsonConvert.SerializeObject("Success"),
                        statusCode = 200,
                        headers = headerValue
                    };
                }
                else
                {
                    LambdaLogger.Log("Empty Request");
                    return new LambdaResponse
                    {
                        isBase64Encoded = false,
                        body = JsonConvert.SerializeObject("Failed: Empty Request"),
                        statusCode = 200,
                        headers = headerValue
                    };
                }

            }
            catch (Exception ex)
            {
                LambdaLogger.Log("Failed :" + ex.Message);
                return new LambdaResponse
                {
                    isBase64Encoded = false,
                    body = JsonConvert.SerializeObject("Failed: " + ex.Message),
                    statusCode = 200,
                    headers = headerValue
                };

            }
        }

        private static void AddMessagestatusLogs(string key, string smsSid, bool isSent, string status, string number, string sender = null, bool isOpened = false, int clicks = 0, int ContactId = 0)
        {
            try
            {
                using (ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(new ConfigurationOptions { EndPoints = { $"{RedisClientConnection}:6379" } }))
                {
                    if (redis.IsConnected)
                    {
                        if (key.Contains("_") && key.Split("_").Length > 1)
                        {
                            //For Batch Status Filter
                            bool isValidSId = int.TryParse(key.Split("_")[0], out var sId);
                            bool isValidBatchId = int.TryParse(key.Split("_")[1], out var batchId);
                            if (isValidSId && isValidBatchId)
                            {
                                IDatabase db = redis.GetDatabase(RedishBatchStatusDB);
                                BatchMessageStatus dr = new BatchMessageStatus { batchId = batchId.ToString(), keyId = sId.ToString(), smsSid = smsSid, isSent = isSent, status = status, number = number, sender = sender, Date = DateTime.UtcNow, isOpened = isOpened, clicks = clicks, ContactId = ContactId };
                                db.StringSet(Guid.NewGuid().ToString(), Newtonsoft.Json.JsonConvert.SerializeObject(dr).ToString());
                                LambdaLogger.Log("Added Batch Record");
                            }
                        }
                        //else
                        //{
                        //    IDatabase db = redis.GetDatabase(RedishStatusDB);
                        //    MessageStatus dr = new MessageStatus { keyId = key, smsSid = smsSid, isSent = isSent, status = status, number = number, sender = sender, Date = DateTime.UtcNow, isOpened = isOpened, clicks = clicks };
                        //    db.StringSet(Guid.NewGuid().ToString(), Newtonsoft.Json.JsonConvert.SerializeObject(dr).ToString());
                        //    LambdaLogger.Log("Added Record");
                        //}
                    }
                    else
                    {
                        throw new ApplicationException("Redis connection error");
                    }
                }
            }
            catch (Exception e)
            {
                LambdaLogger.Log("email status update exceptio:-" + e.StackTrace);
                throw;
            }
        }

        private static void AddClickMapLogs(ClickMap clickMap)
        {
            try
            {
                using (ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(new ConfigurationOptions { EndPoints = { $"{RedisClientConnection}:6379" } }))
                {
                    if (redis.IsConnected)
                    {
                        IDatabase db = redis.GetDatabase(RedishClickMapDB);
                        db.StringSet(Guid.NewGuid().ToString(), Newtonsoft.Json.JsonConvert.SerializeObject(clickMap).ToString());
                        LambdaLogger.Log("Added clickMap Record");
                    }
                    else
                    {
                        throw new ApplicationException("Redis connection error");
                    }
                }
            }
            catch (Exception e)
            {
                LambdaLogger.Log("Added clickMap update exception:-" + e.StackTrace);
                throw;
            }
        }

        public static string FirstCharToUpper(string s)
        {
            // Check for empty string.  
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            // Return char and concat substring.  
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        public class LambdaResponse
        {
            public bool isBase64Encoded { get; set; }
            public Dictionary<string, string> headers { get; set; }
            public int statusCode { get; set; }
            public string body { get; set; }

        }

        public static void ConvertNestedJsonToSimpleJson(JObject input, Dictionary<string, string> output, string prefix = "")
        {
            foreach (JProperty jprop in input.Properties())
            {
                var name = jprop.Name; // prefix == "" ? jprop.Name : String.Format("{0}__{1}", prefix, jprop.Name);
                if (jprop.Children<JObject>().Count() == 0)
                {
                    try
                    {
                        if (!output.TryAdd(name, jprop.Value?.ToString()))
                            output.TryAdd(String.Format("{0}__{1}", prefix, jprop.Name), jprop.Value.ToString());
                    }
                    catch { }
                }
                else
                {
                    ConvertNestedJsonToSimpleJson((JObject)jprop.Value, output, name);
                }
            }
        }

        public static bool ValidateJSON(string s)
        {
            try
            {
                JToken.Parse(s);
                return true;
            }
            catch 
            {
                return false;
            }
        }

    }
}
