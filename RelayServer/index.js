//-- SECTION BEGIN: Create Server --
//ref: https://programmer.group/in-nodejs-http-protocol-and-ws-protocol-reuse-the-same-port.html
var express = require("express");
const http = require("http");//upgrade port for http use...
const WS_MODULE = require("ws");
const path = require("path");

const app = express();
app.use(express.static(__dirname + '/public'));
const port = process.env.PORT || 3000;

app.get("/hello", (req, res) => { res.send("hello world"); });

const server = http.createServer(app);
ws = new WS_MODULE.Server({server});
server.listen(port, () => { console.log("++ Server turned on, port number:" + port); });

// Initialize Firebase Admin (for FCM push-to-wake). The service account key is
// gitignored (RelayServer/secrets/) since it grants full Firebase project access -
// it must be placed manually on any machine running this server. If it's missing,
// push-to-wake is simply disabled (rest of the relay still works normally).
let firebaseMessaging = null;
try {
  const admin = require("firebase-admin");
  const serviceAccount = require(path.join(__dirname, "secrets", "firebase-adminsdk.json"));
  admin.initializeApp({ credential: admin.credential.cert(serviceAccount) });
  firebaseMessaging = admin.messaging();
  console.log("++ Firebase Admin initialized, push-to-wake enabled");
} catch (e) {
  console.log("-- Firebase Admin not initialized (push-to-wake disabled): " + e.message);
}

// Initialize WebSocket connection handling
const { clients, rooms, uuidv4, ByteToInt32, ByteToInt16, initializeWebSocketHandling } = require('./core');
initializeWebSocketHandling(ws, firebaseMessaging);
//-- SECTION END: Create Server --
