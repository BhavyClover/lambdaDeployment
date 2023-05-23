using System;
using System.Collections.Generic;

namespace AWSMailgunCallbackHandler
{
    public class MessageStatus
    {
        public string keyId { get; set; }
        public string smsSid { get; set; }
        public bool isSent { get; set; }
        public string status { get; set; }
        public string number { get; set; }
        public string sender { get; set; }
        public DateTime Date { get; set; }
        public bool isOpened { get; set; }
        public int clicks { get; set; }
    }

    public class BatchMessageStatus
    {
        public string batchId { get; set; }
        public string keyId { get; set; }
        public string smsSid { get; set; }
        public bool isSent { get; set; }
        public string status { get; set; }
        public string number { get; set; }
        public string sender { get; set; }
        public DateTime Date { get; set; }
        public bool isOpened { get; set; }
        public int clicks { get; set; }
        public int ContactId { get; set; }
    }

    public class ClickMap
    {
        public int MessageDataId { get; set; }
        public int ContactId { get; set; }
        public string URL { get; set; }
        public int Clicks { get; set; }

    }
}