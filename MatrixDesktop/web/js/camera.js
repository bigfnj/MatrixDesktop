// TODO: switch to video-based texture
// TODO: mipmap?
const video = document.createElement("video");
const cameraCanvas = document.createElement("canvas");
cameraCanvas.width = 1;
cameraCanvas.height = 1;
const context = cameraCanvas.getContext("2d");
let cameraAspectRatio = 1.0;
const cameraSize = [1, 1];
let _animationFrameId = null;
let _isDrawing = false;
let _stream = null;
let _generation = 0;
const stoppedStreams = new WeakSet();

const stopTracks = (stream) => {
	if (!stream || stoppedStreams.has(stream)) return;
	stoppedStreams.add(stream);
	stream.getTracks().forEach(track => { try { track.stop(); } catch {} });
};

const drawToCanvas = () => {
	if (!_isDrawing) return;
	_animationFrameId = requestAnimationFrame(drawToCanvas);
	if (video.readyState >= 2) {
		context.drawImage(video, 0, 0);
	}
};

const stopCameraLoop = () => {
	_isDrawing = false;
	if (_animationFrameId !== null) {
		cancelAnimationFrame(_animationFrameId);
		_animationFrameId = null;
	}
};

const stopCamera = () => {
	_generation++;
	stopCameraLoop();
	// Stop all tracks to release the camera
	stopTracks(_stream);
	_stream = null;
	video.srcObject = null;
};

const setupCamera = async () => {
	stopCamera();
	const generation = _generation;
	let stream = null;
	try {
		stream = await navigator.mediaDevices.getUserMedia({
			video: {
				width: { min: 800, ideal: 1280 },
				frameRate: { ideal: 60 },
			},
			audio: false,
			});
		if (generation !== _generation) { stopTracks(stream); return; }
		_stream = stream;
		const videoTrack = stream.getVideoTracks()[0];
		const settings = videoTrack.getSettings();
		const width = Math.max(1, settings.width ?? 1);
		const height = Math.max(1, settings.height ?? 1);

		video.width = width;
		video.height = height;
		cameraCanvas.width = width;
		cameraCanvas.height = height;
		cameraAspectRatio = width / height;
		cameraSize[0] = width;
		cameraSize[1] = height;

		video.srcObject = _stream;
		await video.play();
		if (generation !== _generation) { stopTracks(stream); return; }

		_isDrawing = true;
		drawToCanvas();
	} catch (e) {
		stopTracks(stream);
		if (generation !== _generation) return;
		stopCamera();
		console.warn(`Camera not initialized: ${e}`);
	}
};

export { cameraCanvas, cameraAspectRatio, cameraSize, setupCamera, stopCameraLoop, stopCamera };
