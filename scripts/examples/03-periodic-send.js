// Example 3: Periodic Send
// Sends a CAN frame at regular intervals (e.g., heartbeat).

const CAN_ID = 0x123;           // CAN ID to send
const INTERVAL_MS = 100;        // Send interval in milliseconds
const DATA = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]; // Frame data

let sendCount = 0;
let isRunning = true;

async function onInit() {
    console.log(`Periodic Send started: ID=0x${CAN_ID.toString(16)}, Interval=${INTERVAL_MS}ms`);
    console.log(`Data: ${toHex(DATA)}`);

    // Check if channel is connected
    if (!can.isConnected()) {
        console.error("No CAN channel connected! Please connect first.");
        return;
    }

    // Send loop
    while (isRunning) {
        const success = await can.send(CAN_ID, DATA);
        if (success) {
            sendCount++;
            if (sendCount % 100 === 0) {
                console.log(`Sent ${sendCount} frames`);
            }
        } else {
            console.error("Failed to send frame");
        }
        await delay(INTERVAL_MS);
    }
}

function onDispose() {
    isRunning = false;
    console.log(`Periodic Send stopped. Total sent: ${sendCount}`);
}
