// Example 2: DBC Signal Monitor
// Loads a DBC file and monitors decoded signal values.

// Replace with your DBC file path
const DBC_PATH = "C:/path/to/your/network.dbc";

async function onInit() {
    console.log("DBC Signal Monitor started");

    // Load DBC file
    const result = await dbc.load(DBC_PATH);
    if (!result.success) {
        console.error(`Failed to load DBC: ${result.error}`);
        return;
    }

    console.log(`Loaded ${result.messageCount} messages`);

    // List available messages
    const messages = dbc.getMessages();
    console.log("Available messages:");
    messages.forEach(msg => {
        console.log(`  0x${msg.id.toString(16).toUpperCase()}: ${msg.name} (${msg.dlc} bytes) from ${msg.sender}`);
    });

    // Monitor all frames and decode
    can.onFrame((frame) => {
        const decoded = dbc.decode(frame);
        if (decoded) {
            console.log(`\nMessage: ${decoded.message}`);
            Object.entries(decoded.signals).forEach(([name, signal]) => {
                console.log(`  ${name}: ${signal.value.toFixed(2)} ${signal.unit}`);
            });
        }
    });
}

function onDispose() {
    console.log("DBC Signal Monitor stopped");
}
