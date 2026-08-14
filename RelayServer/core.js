const clients = new Map();
const rooms = new Map();
// roomName -> Map<deviceId, fcmToken>. Persists across reconnects/app-restarts (unlike
// wsid, which is random per-connection) so a device can be woken via push even after
// its socket has long since closed. Populated by the 'registerPush' message.
const pushRegistry = new Map();
function uuidv4()
{
  function s4() { return Math.floor((1 + Math.random()) * 0x10000).toString(16).substring(1); }
  return s4() + s4() + '-' + s4();
}
function ByteToInt32(_byte, _offset) { return (_byte[_offset] & 255) + ((_byte[_offset + 1] & 255) << 8) + ((_byte[_offset + 2] & 255) << 16) + ((_byte[_offset + 3] & 255) << 24); }
function ByteToInt16(_byte, _offset) { return (_byte[_offset] & 255) + ((_byte[_offset + 1] & 255) << 8); }
function initializeWebSocketHandling(ws, firebaseMessaging)
{
    console.log("++ initialize WebSocket Handling...")
    ws.on('connection', function connection(ws)
    {
        const wsid = uuidv4();
        const networkType = 'undefined';
        const roomName = 'Lobby';
        const metadata = { ws, wsid, networkType, roomName, deviceId: '' };

        const roomClients = new Map();
        const roomMasterWSID = '';
        const roomInfo = { roomName, roomClients, roomMasterWSID };

        function wakeOtherRegisteredDevices(_inRoomName, _inMyDeviceId)
        {
            if (!firebaseMessaging) return;
            if (!pushRegistry.has(_inRoomName)) return;

            pushRegistry.get(_inRoomName).forEach((token, deviceId) =>
            {
                if (deviceId === _inMyDeviceId) return;
                firebaseMessaging.send({
                    token: token,
                    data: { type: "wake", roomName: _inRoomName },
                    android: { priority: "high" }
                }).then(() =>
                {
                    console.log("++ PUSH sent to deviceId: " + deviceId + " for room [" + _inRoomName + "]");
                }).catch((err) =>
                {
                    console.log("-- PUSH failed for deviceId: " + deviceId + " | code=" + err.code + " | message=" + err.message + " | token(len=" + token.length + ")=" + JSON.stringify(token));
                });
            });
        }

        function JoinOrCreateRoom(_inRoomName, _inID, _inDeviceId)
        {
          if (!rooms.has(_inRoomName))
          {
              console.log("++ ROOM [" + _inRoomName + "] " + "register new room");
              rooms.set(_inRoomName, roomInfo);
          }

          if(!rooms.get(_inRoomName).roomClients.has(_inID))
          {
              console.log("++ ROOM [" + _inRoomName + "] " + "register new client wsid: " + _inID);
              rooms.get(_inRoomName).roomClients.set(_inID, clients.get(_inID));

              //assign roomMasterID
              if(rooms.get(_inRoomName).roomClients.size === 1)
              {
                SetRoomMasterWSID(_inRoomName, wsid);
                //alone in the room - wake up any other registered devices for this room
                wakeOtherRegisteredDevices(_inRoomName, _inDeviceId);
              }
              else if(rooms.get(_inRoomName).roomClients.size > 1)
              {
                try { rooms.get(_inRoomName).roomClients.get(rooms.get(_inRoomName).roomMasterWSID).ws.send(FMEventEncode("OnClientConnectedEvent", wsid)); } catch {}
              }
          }

          //check room list:
          const _roomInfo_clients = rooms.get(_inRoomName).roomClients.values();
          for(var i = 0; i < rooms.get(_inRoomName).roomClients.size; i++)
          {
              console.log("** ROOM [" + _inRoomName + "] " + "(" + rooms.get(_inRoomName).roomMasterWSID + ")" + " client["+ i + "]: " + _roomInfo_clients.next().value.wsid);
          }

          console.log("== OnJoinedRoom(" + _inRoomName +")");
          ws.send(FMEventEncode("OnJoinedRoom", _inRoomName));
        }

        function IsRoomMaster(_inRoomName, _inWSID)
        {
          if (!rooms.has(_inRoomName)) return false;
          return rooms.get(_inRoomName).roomMasterWSID === _inWSID ? true : false;
        }

        function SetRoomMasterWSID(_inRoomName, _inWSID)
        {
          rooms.get(_inRoomName).roomMasterWSID = _inWSID;

          var roomClientsWSIDs ="";
          //check room list:
          var _roomInfo_clients = rooms.get(_inRoomName).roomClients.values();
          for(var i = 0; i < rooms.get(_inRoomName).roomClients.size; i++)
          {
            var roomLocalClient = _roomInfo_clients.next().value;
            roomClientsWSIDs += roomLocalClient.wsid + ",";
          }
          rooms.get(_inRoomName).roomClients.get(rooms.get(_inRoomName).roomMasterWSID).ws.send(FMEventEncode("OnRoomClientsUpdated", roomClientsWSIDs));
          rooms.get(_inRoomName).roomClients.get(rooms.get(_inRoomName).roomMasterWSID).ws.send(FMEventEncode("OnRoomMasterRequestEvent", "registered"));
          console.log("** ROOM [" + _inRoomName + "] " + "SetRoomMasterWSID: " + _inWSID);
        }
        //

        //register it to global clients
        if(!clients.has(wsid))
        {
          ws.id = wsid;
          metadata.ws = ws;
          metadata.networkType = "";
          metadata.wsid = wsid;
          metadata.roomName = 'Lobby';
          clients.set(wsid, metadata);
          ws.send(FMEventEncode("OnReceivedWSIDEvent", wsid));
          console.log("== connection count: " + clients.size + " | wsid: " + wsid);
        }

        //ping from Server, to check connection on server side only..
        //ping from Client will be from Unity side, use string instead, to support WebGL build...
        function heartbeat() { this.isAlive = true; }
        ws.on('pong', heartbeat);
        const interval = setInterval(function ping()
        {
            if (ws.isAlive === false)
            {
              console.log("-- Terminate Timeout WSID: " + ws.wsid);
              return ws.terminate();
            }
            ws.isAlive = false;
            ws.ping();
        }, 30000);

        ws.on('close', function close()
        {
            clearInterval(interval);
            var _networkType = clients.get(wsid).networkType;
            console.log('== ON CLOSE: ' + wsid + " | " + _networkType);
            if(_networkType === 'Room')
            {
                var _roomName = clients.get(wsid).roomName;
                if(rooms.has(_roomName))
                {
                    if(rooms.get(_roomName).roomClients.has(wsid))
                    {
                        rooms.get(_roomName).roomClients.delete(wsid);
                        console.log("-- ROOM [" + _roomName + "] " + "Delete Client: " + wsid + " | client count: " + rooms.get(_roomName).roomClients.size);
                        if(rooms.get(_roomName).roomClients.size === 0)
                        {
                          rooms.delete(_roomName);
                          console.log("-- ROOM [" + _roomName + "] " + "Delete Room" + " | room count: " + rooms.size);
                        }
                        else
                        {
    // Notify every remaining member that this client left, regardless of
                            // whether it was the room master. The peer's app can disappear any
                            // number of ways (swipe-close, crash, force-stop, network loss) that
                            // never give it a chance to send an explicit goodbye message, so this
                            // server-detected TCP-close is the only signal that's reliable in all
                            // of those cases.
                            var _remainingClients = rooms.get(_roomName).roomClients.values();
                            for (var ci = 0; ci < rooms.get(_roomName).roomClients.size; ci++)
                            {
                                var remainingClient = _remainingClients.next().value;
                                try { remainingClient.ws.send(FMEventEncode("OnClientDisconnectedEvent", wsid)); } catch {}
                            }
                            console.log("** Sent OnClientDisconnectedEvent to remaining ROOM [" + _roomName + "] clients | disconnected client wsid: " + wsid);

                            if(rooms.get(_roomName).roomMasterWSID === wsid)
                            {
                                //assign a new room master...
                                var _roomInfo_clients = rooms.get(_roomName).roomClients.values();
                                var roomLocalClient = _roomInfo_clients.next().value;
                                SetRoomMasterWSID(_roomName, roomLocalClient.wsid);
                            }
                        }
                    }
                }
            }

            clients.delete(wsid);
            console.log("== Global Connection Count: " + clients.size);
        });

        //FMEventDecode, assuming data structure: "fmevent:type:variable"
        function FMEventEncode(inputType, inputVariable) { return "fmevent" + ":" + inputType + ":" + inputVariable; }
        function FMEventDecode(inputString) { return inputString.split(":"); }
        ws.on('message', function incoming(data, isBinary)
        {
            if (isBinary === false)
            {
                //data type: string
                var message = data.toString();
                if(message.includes("fmevent:"))
                {
                    var decodedResult = FMEventDecode(message);
                    var decodedType = decodedResult[1];
                    // FCM tokens contain a ':' themselves (e.g. "abc123:APA91b..."), so a plain
                    // decodedResult[2] silently truncates at that inner colon. Rejoin everything
                    // after the type so values containing ':' survive intact.
                    var decodedValue = decodedResult.slice(2).join(':');
                    if (decodedType !== 'ping' && decodedType !== 'lping') console.log("-> Message [" + message + "]");//ignore ping message...

                    switch(decodedType)
                    {
                      case 'lping': // ws latency ping-pong test, the lping is from the queued data...
                          ws.send(FMEventEncode("lpong", decodedValue));
                          break;
                      case 'ping':
                          var myRoomName = clients.get(wsid).roomName;
                          var _isRoomMaster = IsRoomMaster(myRoomName, wsid);
                          ws.send(FMEventEncode("pong", decodedValue + "," + (_isRoomMaster ? "roomMaster" : "roomClient")));
                          break;
                      case 'networkType':
                          clients.get(wsid).networkType = decodedValue;
                          ws.send(FMEventEncode("OnJoinedLobbyEvent", clients.get(wsid).roomName));
                          console.log("regRoom(waiting request): " + wsid + " networkType: " + clients.get(wsid).networkType + " | roomName: " + clients.get(wsid).roomName);
                          break;
                      case 'roomName':
                          {
                              // value format: "roomName" or "roomName,deviceId"
                              var _roomParts = decodedValue.split(',');
                              clients.get(wsid).roomName = _roomParts[0];
                              if (_roomParts.length > 1) clients.get(wsid).deviceId = _roomParts[1];
                              JoinOrCreateRoom(clients.get(wsid).roomName, clients.get(wsid).wsid, clients.get(wsid).deviceId);
                          }
                          break;
                      case 'registerPush':
                          {
                              // value format: "roomName,deviceId,fcmToken"
                              var _pushParts = decodedValue.split(',');
                              if (_pushParts.length >= 3)
                              {
                                  var _pushRoomName = _pushParts[0];
                                  var _pushDeviceId = _pushParts[1];
                                  var _pushToken = _pushParts.slice(2).join(','); // token itself may contain characters, but not ':' (fmevent separator); commas are not expected either, this is defensive
                                  if (!pushRegistry.has(_pushRoomName)) pushRegistry.set(_pushRoomName, new Map());
                                  pushRegistry.get(_pushRoomName).set(_pushDeviceId, _pushToken);
                                  console.log("++ registerPush room [" + _pushRoomName + "] deviceId [" + _pushDeviceId + "]");
                              }
                          }
                          break;
                      case 'unregisterPush':
                          {
                              // value format: "roomName,deviceId" - lets a device that no
                              // longer wants to participate (see CamStart.OnClickLeaveRoomPermanently)
                              // remove itself from the wake-push list. Without this, pushRegistry
                              // keeps sending it wake pushes forever (it's keyed by deviceId, not
                              // tied to the current connection, so it otherwise only clears on
                              // server restart).
                              var _unregParts = decodedValue.split(',');
                              if (_unregParts.length >= 2)
                              {
                                  var _unregRoomName = _unregParts[0];
                                  var _unregDeviceId = _unregParts[1];
                                  if (pushRegistry.has(_unregRoomName))
                                  {
                                      pushRegistry.get(_unregRoomName).delete(_unregDeviceId);
                                      console.log("-- unregisterPush room [" + _unregRoomName + "] deviceId [" + _unregDeviceId + "]");
                                  }
                              }
                          }
                          break;
                      case 'requestRoomMaster':
                          var myRoomName = clients.get(wsid).roomName;
                          ws.send(FMEventEncode("OnRoomMasterRequestEvent", "requested"));
                          SetRoomMasterWSID(clients.get(wsid).roomName, wsid);
                          break;
                    }
                }
            }
            else
            {
                //data type: binary bytes
                if(data.length > 4)
                {
                    if(clients.get(wsid).networkType === 'Room')
                    {
                        if(clients.get(wsid).roomName !== 'Lobby')
                        {
                            var myRoomName = clients.get(wsid).roomName;
                            var _roomInfo_clients = rooms.get(myRoomName).roomClients.values();
                            var _roomClientMyself;
                            switch(data[1])
                            {
                                case 0: //emit type: all; //check room list:
                                    for(var i = 0; i < rooms.get(myRoomName).roomClients.size; i++)
                                    {
                                      var roomLocalClient = _roomInfo_clients.next().value;
                                      if (roomLocalClient.wsid !== wsid) { roomLocalClient.ws.send(data); }
                                      else { _roomClientMyself = roomLocalClient; }
                                    }
                                    if (_roomClientMyself) _roomClientMyself.ws.send(data);
                                    break;
                                case 1: //emit type: room Master;
                                    try { rooms.get(myRoomName).roomClients.get(rooms.get(myRoomName).roomMasterWSID).ws.send(data); } catch {}
                                    break;
                                case 2: //emit type: others; //check room list:
                                    for(var i = 0; i < rooms.get(myRoomName).roomClients.size; i++)
                                    {
                                      var roomLocalClient = _roomInfo_clients.next().value;
                                      if (roomLocalClient.wsid !== wsid) roomLocalClient.ws.send(data);
                                      // console.log("room client["+ i + "]: " + _roomInfo_clients.next().value.wsid);
                                    }
                                    break;
                                case 3: //send to target
                                    var _wsidByteLength = ByteToInt16(data, 4);
                                    var _wsidByte = data.slice(6, 6 + _wsidByteLength);
                                    var _wsid = String.fromCharCode(..._wsidByte);
                                    try { if (clients.get(_wsid).roomName === myRoomName) clients.get(_wsid).ws.send(data); } catch{}
                                    break;
                            }
                        }
                    }
                }
            }
        });
    });
}

module.exports = {
  clients,
  rooms,
  pushRegistry,
  uuidv4,
  ByteToInt32,
  ByteToInt16,
  initializeWebSocketHandling
};
