// Example 6: Bus Load Generator
// Sends frames at a configurable rate to stress-test the bus.

const TARGET_FPS = 1000;        // Target frames per second
const CAN_ID = 0x100;           // Starting CAN ID
const DATA_LENGTH = 8;          // Frame data length

let sentCount = 0;
let errorCount = 0;
let isRunning = true;
let startTime = 0;

async function onInit() {
    console.log(`Bus Load Generator started: Target=${TARGET_FPS} fps`);
    console.log(`CAN ID: 0x${CAN_ID.toString(16)}, Data length: ${DATA_LENGTH} bytes`);

    if (!can.isConnected()) {
        console.error("No CAN channel connected!");
        return;
    }

    startTime = Date.now();
    const intervalMs = 1000 / TARGET_FPS;

    // Generate data
    const data = new Array(DATA_LENGTH).fill(0);

    // Send loop
    while (isRunning) {
        // Increment data for visual feedback
        data[0] = (data[0] + 1) & 0xFF;

        const success = await can.send(CAN_ID, data);
        if (success) {
            sentCount++;
        } else {
            errorCount++;
        }

        // Report every second
        const elapsed = Date.now() - startTime;
        if (elapsed >= 1000 && sentCount % 100 === 0) {
            const actualFps = (sentCount / elapsed) * 1000;
            const loadPercent = (actualFps / TARGET_FPS * 100).toFixed(1);
            console.log(`Sent: ${sentCount} | Errors: ${errorCount} | FPS: ${actualFps.toFixed(0)} | Load: ${loadPercent}%`);
        }

        await delay(intervalMs);
    }
}

function onDispose() {
    isRunning = false;
    const elapsed = Date.now() - startTime;
    const avgFps = (sentCount / elapsed) * 1000;
    console.log(`\nBus Load Generator stopped`);
    console.log(`Total sent: ${sentCount} | Errors: ${errorCount} | Avg FPS: ${avgFps.toFixed(0)}`);
}
