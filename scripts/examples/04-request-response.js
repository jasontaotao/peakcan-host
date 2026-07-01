// Example 4: Request-Response
// Sends a diagnostic request and waits for response.

const REQUEST_ID = 0x7DF;      // OBD-II functional request ID
const RESPONSE_ID = 0x7E8;     // OBD-II response ID
const TIMEOUT_MS = 1000;       // Response timeout

// OBD-II Service 01, PID 00 (supported PIDs request)
const REQUEST_DATA = [0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

let pendingResolve = null;

async function onInit() {
    console.log("OBD-II Request-Response Example");
    console.log(`Sending request to 0x${REQUEST_ID.toString(16)}...`);

    // Register response handler
    can.onMessage(RESPONSE_ID, (frame) => {
        console.log(`Response received: ${toHex(Array.from(frame.Data))}`);
        if (pendingResolve) {
            pendingResolve(frame);
            pendingResolve = null;
        }
    });

    // Send request
    const success = await can.send(REQUEST_ID, REQUEST_DATA);
    if (!success) {
        console.error("Failed to send request");
        return;
    }

    // Wait for response with timeout
    try {
        const response = await new Promise((resolve, reject) => {
            pendingResolve = resolve;
            setTimeout(() => {
                pendingResolve = null;
                reject(new Error("Response timeout"));
            }, TIMEOUT_MS);
        });

        console.log("Request completed successfully");
    } catch (error) {
        console.error(`Error: ${error.message}`);
    }
}

function onDispose() {
    console.log("Request-Response example stopped");
}
