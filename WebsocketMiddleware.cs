﻿/*****************************************************************************************************************
 * Author: Ajantha Dhanasekaran
 * Date: 07-Sept-2020
 * Purpose: Middleware to handle websocket connections and payload from the OCPP charger client.
 * Change History:
 * Name                         Date                    Change description
 * Ajantha Dhanasekaran         07-Sept-2020              Initial version
 * Ajantha Dhanasekaran         09-Sept-2020              Enabled json validation
 * Ajantha Dhanasekaran         15-Sept-2020              Enabled common logger
 * Ajantha Dhanasekaran         16-Sept-2020              Misc. logger changes
 * ****************************************************************************************************************/


#region license

/*
Cognizant EV Charging Protocol Gateway 1.0
© 2020 Cognizant. All rights reserved.
"Cognizant EV Charging Protocol Gateway 1.0" by Cognizant  is licensed under Apache License Version 2.0


Copyright 2020 Cognizant


Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at


    http://www.apache.org/licenses/LICENSE-2.0


Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

#endregion

#region Libraries
using ChargePointOperator.Models;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Schema;
using ChargePointOperator.Models.OCPP;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net;
using System.Collections.Concurrent;
using ChargePointOperator.Models.Internal;
using ProtocolGateway;
using ProtocolGateway.Models;
using Microsoft.Extensions.Configuration;
using System.IO;
#endregion

namespace ChargePointOperator
{
    public class WebsocketMiddleware
    {
        #region Members
        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;
        private readonly Logger _logger;
        HttpContext context;

        private List<string> knownChargers = new List<string>();
        public static ConcurrentDictionary<string, Charger> activeCharger = new ConcurrentDictionary<string, Charger>();
        private IGatewayClient _gatewayClient;
        private string _logURL;

        #endregion

        #region Constructors
        public WebsocketMiddleware(RequestDelegate next,IConfiguration configuration,IGatewayClient gatewayClient)
        {
            _next = next;
            _configuration = configuration;

            _logURL = _configuration["LogURL"];
            _logger=new Logger();

            _gatewayClient =gatewayClient;
            _gatewayClient.SetSendToChargepointMethod(CallSendMethodAsync);
        }

        public WebsocketMiddleware()
        {
        }

        #endregion

        /// <summary>
        /// This method handles all the http request passed on by the previous middleware
        /// </summary>
        /// <param name="httpContext"></param>
        /// <returns></returns>
        public async Task Invoke(HttpContext httpContext)
        {

            context = httpContext;

            try
            {
                _logger.LogInformation("Request starting");

                //Only accepts url that contains /ocpp
                if (httpContext.Request.Path.Value.Contains(StringConstants.RequestPath))

                    //Only accepts websocket request
                    if (httpContext.WebSockets.IsWebSocketRequest)
                    {
                        await HandleWebsockets(httpContext);

                        return;
                    }

                //Request passed on to next middleware
                await _next(httpContext);
            }
            catch (Exception e)
            {
                _logger.LogError(httpContext.Request.Path.Value.Split('/').LastOrDefault(),"Invoke",e);

                httpContext.Response.StatusCode=StatusCodes.Status500InternalServerError;
                await httpContext.Response.WriteAsync("Something went wrong!!. Please check with the Central system admin");
            }
            _logger.LogDebug("Request finished.");
        }

        /// <summary>
        /// This method verifies whether the charger supports protoc0l OCPP1.6 or not
        /// </summary>
        /// <param name="httpContext"></param>
        /// <param name="chargepointName">charger Id</param>
        /// <returns></returns>

        private async Task<bool> CheckProtocolAsync(HttpContext httpContext, string chargepointName)
        {
            var errorMessage = string.Empty;

            var chargerProtocols = httpContext.WebSockets.WebSocketRequestedProtocols;
            _logger.LogInformation($"Charger requested protocols : {chargerProtocols} for {chargepointName}");


            if (chargerProtocols.Count == 0)
                errorMessage = StringConstants.NoProtocolHeaderMessage;
            else
            {

                if (!chargerProtocols.Contains(StringConstants.RequiredProtocol)) //Allow only ocpp1.6
                    errorMessage = StringConstants.SubProtocolNotSupportedMessage;
                else
                    return true;
            }

            _logger.LogInformation($"Protocol conflict for {chargepointName}");

            //Websocket request with Protcols that are not supported are accepted and closed
            var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
            await socket.CloseOutputAsync(WebSocketCloseStatus.ProtocolError, errorMessage, CancellationToken.None);

            return false;

        }


        #region WebsocketHandler

        /// <summary>
        /// This method accepts the websocket connnetion from the charger and adds it to the active chargers
        /// </summary>
        /// <param name="httpContext"></param>
        /// <returns></returns>
        private async Task HandleWebsockets(HttpContext httpContext)
        {
            _logger.LogDebug($"Entering HandleWebsockets method");
            string chargepointName = string.Empty;
            try
            {
                string requestPath = httpContext.Request.Path.Value;
                chargepointName = requestPath.Split('/').LastOrDefault();


                if (!knownChargers.Contains(chargepointName))
                {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                }

                if (!await CheckProtocolAsync(httpContext, chargepointName))
                   return;

                var socket = await httpContext.WebSockets.AcceptWebSocketAsync(StringConstants.RequiredProtocol);

                if (socket == null || socket.State != WebSocketState.Open)
                {
                    await _next(httpContext);
                    return;
                }

                if (!activeCharger.ContainsKey(chargepointName))
                {
                    activeCharger.TryAdd(chargepointName, new Charger(chargepointName, socket));
                    _logger.LogInformation($"No. of active chargers : {activeCharger.Count}");
                }
                else
                {
                    try
                    {
                        var oldSocket = activeCharger[chargepointName].WebSocket;
                        activeCharger[chargepointName].WebSocket = socket;
                        if (oldSocket != null)
                        {
                            _logger.LogWarning($"New websocket request received for {chargepointName}");
                            if (oldSocket != socket && oldSocket.State != WebSocketState.Closed)
                            {
                                _logger.LogWarning($"Closing old websocket for {chargepointName}");

                                await oldSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, StringConstants.ClientInitiatedNewWebsocketMessage, CancellationToken.None);
                            }
                        }
                        _logger.LogWarning($"Websocket replaced successfully for {chargepointName}");
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(chargepointName,"While closing old socket in HandleWebsockets",e);
                    }
                }


                if (socket.State == WebSocketState.Open)
                    await HandleActiveConnection(socket, chargepointName);

            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"HandleWebsockets",e);
            }

            _logger.LogDebug($"Exiting HandleWebsockets method for {chargepointName}");

        }

        /// <summary>
        /// This method handles all the active websocket connections
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="chargepointName"></param>
        /// <returns></returns>
        private async Task HandleActiveConnection(WebSocket webSocket, string chargepointName)
        {
            _logger.LogDebug($"Entering HandleActiveConnections method for {chargepointName}");
            _logger.LogInformation($"Websocket connected for {chargepointName}");
            try
            {
                if (webSocket.State == WebSocketState.Open)
                    await HandlePayloadsAsync(chargepointName, webSocket);

                if (webSocket.State != WebSocketState.Open && activeCharger.ContainsKey(chargepointName) && activeCharger[chargepointName].WebSocket == webSocket)
                    await RemoveConnectionsAsync(chargepointName, webSocket);

            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"HandleActiveConnections",e);
            }
            _logger.LogDebug($"Exiting HandleActiveConnections method for {chargepointName}");
        }

        #endregion

        #region PayloadHandler

        /// <summary>
        /// This method receives data from the charger through websocket
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="chargepointName">charger Id</param>
        /// <returns></returns>
        private async Task<string> ReceiveDataFromChargerAsync(WebSocket webSocket, string chargepointName)
        {
            _logger.LogDebug($"Receiving payload from charger {chargepointName}");

            try
            {
                ArraySegment<byte> data = new ArraySegment<byte>(new byte[1024]);
                WebSocketReceiveResult result;
                string payloadString = string.Empty;

                do
                {
                    result = await webSocket.ReceiveAsync(data, CancellationToken.None);

                    //When the charger sends close frame
                    if (result.CloseStatus.HasValue)
                    {
                        if (webSocket != activeCharger[chargepointName].WebSocket)
                        {
                            if(webSocket.State!=WebSocketState.CloseReceived)
                                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, StringConstants.ChargerNewWebRequestMessage, CancellationToken.None);
                        }
                        else
                            await RemoveConnectionsAsync(chargepointName, webSocket);
                        return null;
                    }

                    //Appending received data
                    payloadString += Encoding.UTF8.GetString(data.Array, 0, result.Count);

                } while (!result.EndOfMessage);

                _logger.LogTrace($"Data from charger {chargepointName} : {payloadString}");
                return payloadString;

            }
            catch (WebSocketException websocex)
            {
                if (webSocket != activeCharger[chargepointName].WebSocket)
                    _logger.LogWarning($"WebsocketException occured in the old socket while receiving payload from charger {chargepointName}. Error : {websocex.Message}");
                else
                    _logger.LogError(chargepointName,"ReceiveDataFromChargerAsync",websocex);
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"ReceiveDataFromChargerAsync",e);

            }
            _logger.LogDebug($"Exiting Receive method for charger {chargepointName}");
            return null;
        }

        /// <summary>
        /// This method sends payload to the charger
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="payload"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task SendPayloadToChargerAsync(string chargepointName, object payload, WebSocket webSocket)
        {
            _logger.LogDebug($"Sending payload to charger {chargepointName}");
            var charger = activeCharger[chargepointName];

            try
            {
                charger.WebsocketBusy = true;

                var settings = new JsonSerializerSettings { DateFormatString = StringConstants.DateTimeFormat, NullValueHandling = NullValueHandling.Ignore };
                var serializedPayload = JsonConvert.SerializeObject(payload, settings);

                _logger.LogTrace($"Serialized Payload : {serializedPayload} for {chargepointName}");

                ArraySegment<byte> data = Encoding.UTF8.GetBytes(serializedPayload);

                if (webSocket.State == WebSocketState.Open)
                    await webSocket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"SendPayloadToCharger",e);
            }

            charger.WebsocketBusy = false;

        }

        /// <summary>
        /// This method processes the received payload from the charger
        /// </summary>
        /// <param name="payloadString"></param>
        /// <param name="chargepointName"></param>
        /// <returns></returns>
        private JArray ProcessPayload(string payloadString, string chargepointName)
        {
            _logger.LogDebug($"Processing payload for charger {chargepointName}");
            try
            {
                if (payloadString != null)
                {
                    _logger.LogTrace($"Input payload string : {payloadString}");
                    var basePayload = JsonConvert.DeserializeObject<JArray>(payloadString);
                    return basePayload;

                }
                else
                    _logger.LogWarning($"Null payload received for {chargepointName}");
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"ProcessPayload",e);
            }
            _logger.LogDebug($"Exiting processpayload method for charger {chargepointName}");
            return null;
        }

        /// <summary>
        /// This method validates payload received from the charger against the COPP schema.
        /// Remarks : Free version allows verification of first 1000 words alone.
        /// </summary>
        /// <param name="payload"></param>
        /// <param name="action"></param>
        /// <param name="chargepointName"></param>
        /// <returns></returns>
        private JsonValidationResponse JsonValidation(JObject payload, string action, string chargepointName)
        {
            _logger.LogDebug($"Entering Jsonvalidation for {chargepointName}");

            JsonValidationResponse response = new JsonValidationResponse { Valid = false };

            try
            {
               if (action != null)
               {
                   _logger.LogInformation($"Validating payload for {chargepointName} for action {action}.");
                   //Getting Schema FilePath
                   string currentDirectory = Directory.GetCurrentDirectory();
                   string filePath = Path.Combine(currentDirectory, "Schemas", $"{action}.json");

                   //Parsing schema
                   JObject content = JObject.Parse(File.ReadAllText(filePath));
                   JSchema schema = JSchema.Parse(content.ToString());
                   JToken json = JToken.Parse(payload.ToString()); // Parsing input payload

                   // Validate json
                   response = new JsonValidationResponse
                   {
                       Valid = json.IsValid(schema, out IList<ValidationError> errors),
                       Errors = errors.ToList()
                   };

               }
               else
                   _logger.LogError(chargepointName,"JsonValidation","Action is null");
            }
            catch (FileNotFoundException)
            {
                response.CustomErrors = StringConstants.NotImplemented;
            }
            catch (JsonReaderException jsre)
            {
               _logger.LogError(chargepointName,"JsonValidation",jsre);
            }
            catch (Exception e)
            {

               _logger.LogError(chargepointName,"JsonValidation",e);
            }
            _logger.LogDebug($"Exiting Jsonvalidation for {chargepointName}");

            return response;
        }

        /// <summary>
        /// This method processes request payload received from the charger
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="requestPayload"></param>
        /// <returns>JArray of responsePayload or ErrorPayload</returns>
        private async Task<JArray> ProcessRequestPayloadAsync(string chargepointName, RequestPayload requestPayload)
        {
            _logger.LogDebug($"Processing requestPayload for charger {chargepointName}");
            string action = string.Empty;
            try
            {

                await LogPayloads(new LogPayload(requestPayload, chargepointName), chargepointName);

                action = requestPayload.Action;

                var isValidPayload = JsonValidation(requestPayload.Payload, action, chargepointName);

                if (isValidPayload.Valid)
                {
                    _logger.LogInformation($"{action} request received for charger {chargepointName}");

                    object responsePayload = null;
                    string url = string.Empty;

                    //switching based on OCPP action name
                    switch (action)
                    {

                        case "BootNotification":

                            responsePayload = await _gatewayClient.SendBootNotificationAsync(requestPayload,chargepointName);

                            break;

                        case "Authorize":

                            await _gatewayClient.SendTransactionMessageAsync(requestPayload,chargepointName);

                            break;

                        case "StartTransaction":

                           await _gatewayClient.SendTransactionMessageAsync(requestPayload,chargepointName);

                            break;

                        case "StopTransaction":

                           await _gatewayClient.SendTransactionMessageAsync(requestPayload,chargepointName);
                            break;

                        case "Heartbeat":

                            responsePayload = new ResponsePayload(requestPayload.UniqueId, new { currentTime = DateTime.UtcNow });
                            HeartBeatRequest heartBeatRequest = new HeartBeatRequest(chargepointName);

                            await _gatewayClient.SendTelemetryAsync(heartBeatRequest,chargepointName);

                            break;

                        case "MeterValues":


                            MeterValues meterValues = requestPayload.Payload.ToObject<MeterValues>();
                            responsePayload = new ResponsePayload(requestPayload.UniqueId, new object());

                            foreach (var i in meterValues.MeterValue)
                            {
                                foreach (var j in i.sampledValue)
                                {
                                    if (Regex.IsMatch(j.unit.ToString(), @"^(W|Wh|kWh|kW)$"))
                                    {
                                        MeterValueRequest meterValueRequest = new MeterValueRequest(j, chargepointName, meterValues.ConnectorId);
                                        await _gatewayClient.SendTelemetryAsync(meterValueRequest,chargepointName);
                                    }
                                }

                            }
                            break;

                        case "StatusNotification":

                            StatusNotification statusNotification = requestPayload.Payload.ToObject<StatusNotification>();
                            responsePayload = new ResponsePayload(requestPayload.UniqueId, new object());
                            StatusNotificationAzure statusNotificationRequest = new StatusNotificationAzure(statusNotification, chargepointName);

                            await _gatewayClient.SendTelemetryAsync(statusNotificationRequest,chargepointName);

                            break;

                        case "DataTransfer":
                            //<Placeholder>
                            break;

                        case "DiagnosticsStatusNotification":
                            //<Placeholder>
                            break;

                        case "FirmwareStatusNotification":
                            //<Placeholder>
                            break;

                        default:

                            responsePayload = new ErrorPayload(requestPayload.UniqueId, StringConstants.NotImplemented);
                            break;

                    }

                    if(responsePayload!=null)
                    {
                    if (((BasePayload)responsePayload).MessageTypeId == 3)
                    {
                        ResponsePayload response = (ResponsePayload)responsePayload;
                        await LogPayloads(new LogPayload(action, response, chargepointName), chargepointName);
                        return response.WrappedPayload;
                    }
                    else
                    {
                        ErrorPayload error = (ErrorPayload)responsePayload;
                        await LogPayloads(new LogPayload(action, error, chargepointName), chargepointName);
                        return error.WrappedPayload;
                    }
                    }

                }
                else
                {
                    ErrorPayload errorPayload = new ErrorPayload(requestPayload.UniqueId);
                    GetErrorPayload(isValidPayload, errorPayload);

                    await LogPayloads(new LogPayload(action,errorPayload, chargepointName), chargepointName);

                    return errorPayload.WrappedPayload;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,$"ProcessREquestPayload for action {action}",e);
            }
            _logger.LogDebug($"Exiting Process request payload for {chargepointName}");
            return null;

        }

        /// <summary>
        /// This method processes the response payload from the charger
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="responsePayload"></param>
        /// <returns></returns>
        private async Task ProcessResponsePayloadAsync(string chargepointName, ResponsePayload responsePayload)
        {
            _logger.LogDebug($"Processing responsePayload for charger {chargepointName}");

            await Task.Delay(1000);
            //Placeholder to process response payloads from charger for CentralSystem initiated commands
            _logger.LogDebug($"Exiting Process response payload for {chargepointName}");

        }


        /// <summary>
        /// This method processes error payload from the charger
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="errorPayload"></param>
        /// <returns></returns>
        private async Task ProcessErrorPayloadAsync(string chargepointName, ErrorPayload errorPayload)
        {
            //Placeholder to process error payloads from charger for CentralSystem initiated commands
            await Task.Delay(1000);

            _logger.LogDebug($"Exiting Process error payload for {chargepointName}");
        }

        /// <summary>
        /// This method removes connection from the activecharger dictionary
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>

        private async Task RemoveConnectionsAsync(string chargepointName, WebSocket webSocket)
        {
            try
            {
                _logger.LogDebug($"Removing connection for charger {chargepointName}");

                if (activeCharger.TryRemove(chargepointName, out Charger charger))
                    _logger.LogDebug($"Removed charger {chargepointName}");
                else
                    _logger.LogDebug($"Cannot remove charger {chargepointName}");

                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, StringConstants.ClientRequestedClosureMessage, CancellationToken.None);
                _logger.LogDebug($"Closed websocket for charger {chargepointName}. Remaining active chargers : {activeCharger.Count}");

            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"RemoveConnectionsAsync",e);
            }
        }

        /// <summary>
        /// This method gets the error payload for the given non correct request payload
        /// </summary>
        /// <param name="response"></param>
        /// <param name="errorPayload"></param>
        private void GetErrorPayload(JsonValidationResponse response, ErrorPayload errorPayload)
        {
            if (response.Errors != null)
                errorPayload.Payload = JObject.FromObject(new { Error = response.Errors });
            else
                errorPayload.Payload = JObject.FromObject(new object());
            errorPayload.ErrorDescription = string.Empty;

            if (response.CustomErrors != null)
                errorPayload.ErrorCode = "NotImplemented";
            else if (response.Errors == null || response.Errors.Count > 1)
                errorPayload.ErrorCode = "GenericError";
            else
                switch (response.Errors[0].ErrorType)
                {

                    case ErrorType.MultipleOf:
                    case ErrorType.Enum:
                        errorPayload.ErrorCode = "PropertyConstraintViolation";
                        break;

                    case ErrorType.Required:
                    case ErrorType.Format:
                    case ErrorType.AdditionalProperties:
                        errorPayload.ErrorCode = "FormationViolation";
                        break;

                    case ErrorType.Type:
                        errorPayload.ErrorCode = "TypeConstraintViolation";
                        break;

                    default:
                        errorPayload.ErrorCode = "GenericError";
                        break;
                }


        }

        /// <summary>
        /// This method receives payload ; checks the messageTypeId and calls the appropriate processing method
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task HandlePayloadsAsync(string chargepointName, WebSocket webSocket)
        {
            _logger.LogDebug($"Entering HandlePayloads method for {chargepointName}");

            try
            {

                while (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        string payloadString = await ReceiveDataFromChargerAsync(webSocket, chargepointName);
                        var payload = ProcessPayload(payloadString, chargepointName);

                        if (payload != null)
                        {
                            JArray response = null;

                            //switching based on messageTypeId
                            switch ((int)payload[0])
                            {
                                case 2:
                                    RequestPayload requestPayload = new RequestPayload(payload);
                                    _logger.LogTrace(JsonConvert.SerializeObject(requestPayload));
                                    response = await ProcessRequestPayloadAsync(chargepointName, requestPayload);
                                    break;

                                case 3:
                                    ResponsePayload responsePayload = new ResponsePayload(payload);
                                    await ProcessResponsePayloadAsync(chargepointName, responsePayload);
                                    break;

                                case 4:
                                    ErrorPayload errorPayload = new ErrorPayload(payload);
                                    await ProcessErrorPayloadAsync(chargepointName, errorPayload);
                                    continue;

                                default:
                                    break;
                            }

                            if (response != null)
                                await SendPayloadToChargerAsync(chargepointName, response, webSocket);

                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(chargepointName,"HandlePayloads - websocket - open" ,e);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,"HandlePayloads",e);
            }
            _logger.LogDebug($"Exiting HandlePayloads method for {chargepointName}");
        }

#endregion

        /// <summary>
        /// This method logs the payload in case LogURL appsetting is set
        /// </summary>
        /// <param name="logPayload"></param>
        /// <param name="chargepointName"></param>
        /// <returns></returns>
        private async Task LogPayloads(LogPayload logPayload, string chargepointName)
        {

            //Incase LogURL is not provided
            if(string.IsNullOrEmpty(_logURL))
                return;

            _logger.LogTrace($"Logging payloads from charger {chargepointName}");

            try
            {
                var content = new StringContent(JsonConvert.SerializeObject(logPayload), Encoding.UTF8, StringConstants.RequestContentFormat);

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.PostAsync(_logURL, content);

                    if (response.StatusCode == HttpStatusCode.OK)
                        _logger.LogDebug($"{logPayload.Command} Payload logged successfully for {chargepointName}");
                    else
                        _logger.LogWarning($"{response.StatusCode} received while logging payloads for {chargepointName}.");


                }
            }
            catch (Exception e)
            {
                _logger.LogError(chargepointName,$"LogPayload for {logPayload.Command}",e);
            }

        }

        /// <summary>
        /// This method calls the SendPayloadToCharger, this is used in the GatewayClient
        /// </summary>
        /// <param name="chargepointName"></param>
        /// <param name="responsePayload"></param>
        /// <returns></returns>

        private async Task CallSendMethodAsync(string chargepointName,object responsePayload)
        {
            await SendPayloadToChargerAsync(chargepointName,responsePayload,activeCharger[chargepointName].WebSocket);
        }
    }
}
