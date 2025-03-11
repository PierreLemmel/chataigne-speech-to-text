var lastKeepAliveTime = 0;

function logProperties(input) {
    var properties = util.getObjectProperties(input);
    script.log(properties);
}

function logMethods(input) {
    var methods = util.getObjectMethods(input);
    script.log(methods);
}

function init() {
    local.parameters.oscInput.setCollapsed(true);
    local.parameters.oscOutputs.setCollapsed(true);

    local.scripts.setCollapsed(true);
    local.scripts.getChild("speech-to-text").enableLog.set(true);
    
    lastKeepAliveTime = util.getTime();
}

function moduleParameterChanged(param) {

    if (param.is(local.parameters.transcription.launchScript)) {
        script.log("LaunchScript changed, consider restarting the script");
    }

    if (param.is(local.parameters.oscOutputs.oscOutput.remotePort)) {
        script.log("OSC output port changed, consider restarting the script");
    }

    if (param.is(local.parameters.oscInput.localPort)) {
        script.log("OSC input port changed, consider restarting the script");
    }


    if (param.niceName === "Start Transcription") {
        startTranscription();
    }
    
    if (param.niceName === "Stop Transcription") {
        stopTranscription();
    }

    if (param.niceName === "Clear values") {
        clearValues();
    }
}

function update() {
    
    var isRunning = local.parameters.transcriptionRunning.get();
    if (isRunning) {
        var time = util.getTime();
        
        if (time - lastKeepAliveTime > 5) {
            script.log("Stopping transcription due to inactivity");
            local.parameters.transcriptionRunning.set(false);
        }
    }
}

function startTranscription() {

    if (local.parameters.transcriptionRunning.get()) {
        script.log("Transcription is already running, can't start it!");
        return;
    }

    script.log("Starting transcription");
    var launchScript = local.parameters.transcription.launchScript.get();
    if (launchScript === "") {
        script.log("No launch script specified. Add the path to the 'speech-server.bat' file.");
        return;
    }

    var oscInput = local.parameters.oscInput.localPort.get();
    var oscOutput = local.parameters.oscOutputs.oscOutput.remotePort.get();

    var oscIp = local.parameters.oscOutputs.oscOutput.remoteHost.get();

    var gcloudId = local.parameters.transcription.googleCloudProjectID.get();
    var credentials = local.parameters.transcription.googleCloudCredentials.get();

    var device = local.parameters.transcription.microphone.get();

    var args = [
        "--osc-in", oscOutput,
        "--osc-out", oscInput,
        "--ip", oscIp,
        "--gcloud-project-id", gcloudId,
        "--credentials", credentials,
        "--device", device
    ].join(" ");

    script.log("Launching: '" + launchScript + " " + args + "'");
    lastKeepAliveTime = util.getTime();
    util.launchFile(launchScript, args);
}

function stopTranscription() {

    if (!local.parameters.transcriptionRunning.get()) {
        script.log("Transcription is not running, can't stop it!");
        return;
    }
    local.send("/transcription/stop");
}

var sentencesDic = {

};
var nextIndex = 0;

function oscEvent(address, args) {
    if (address.startsWith("/transcription/started/")) {

        var id = extractIdFromAddress(address);
        var startTime = args[0];
        createValueItem(id, startTime);
    }
    if (address.startsWith("/transcription/elaborating/")) {
        
        var id = extractIdFromAddress(address);
        
        var stable = args[0];
        var unstable = args[1];
        var startTime = args[2];

        var container = getValueItem(id, startTime);
        logProperties(stableText);
        logMethods(stableText);
        container.stableText.set(stable);
        container.unstableText.set(unstable);
    }
    if (address.startsWith("/transcription/finalized/")) {
        
        var id = extractIdFromAddress(address);
        
        var text = args[0];
        var startTime = args[1];
        var endTime = args[2];

        var container = getValueItem(id, startTime);
        container.finalized.set(true);
        container.stableText.set(text);
        container.unstableText.set("");

        container.addFloatParameter("EndTime", "Sentence end time", endTime);
    }
    else if (address === "/transcription/keepalive") {
        lastKeepAliveTime = util.getTime();
    }
    else if (address === "/transcription/started") {
        local.parameters.transcriptionRunning.set(true);
        script.log("Transcription started");
    }
    else if (address === "/transcription/stopped") {
        local.parameters.transcriptionRunning.set(false);
        script.log("Transcription stopped");
    }
    else {
        script.log("Unknown OSC message: " + address);
    }
}

function extractIdFromAddress(address) {
    var chunks = address.split("/");
    return chunks[chunks.length - 1];
}

function createValueItem(id, startTime) {

    var container = root.modules.speechToText.values.sentences.addContainer(nextIndex);
    sentencesDic[id] = nextIndex;

    nextIndex++;

    container.addStringParameter("Id", "Sentence Id", id);
    container.addFloatParameter("StartTime", "Sentence start time", startTime);
    container.addBoolParameter("Finalized", "Has sentence been finalized", false);
    container.addStringParameter("Stable text", "Part of text that is stable", "");
    container.addStringParameter("Unstable text", "Part of text that is stable", "");

    return container;
}

function getValueItem(id, startTime) {

    var index = sentencesDic[id];
    if (index !== undefined) {
        return root.modules.speechToText.values.sentences["" + index];
    }
    else {
        return createValueItem(id, startTime);
    }
}

function clearValues() {
    root.modules.speechToText.values.removeContainer("sentences");
    
    root.modules.speechToText.values.addContainer("sentences");

    nextIndex = 0;
    sentencesDic = {};
}