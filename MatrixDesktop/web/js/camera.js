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

const drawToCanvas = () => {
	if (!_isDrawing) return;
	_animationFrameId = requestAnimationFrame(drawToCanvas);
	context.drawImage(video, 0, 0);
};

const stopCameraLoop = () => {
	_isDrawing = false;
	if (_animationFrameId !== null) {
		cancelAnimationFrame(_animationFrameId);
		_animationFrameId = null;
	}
};

const stopCamera = () => {
	stopCameraLoop();
	// Stop all tracks to release the camera
	if (_stream) {
		_stream.getTracks().forEach(track => track.stop());
		_stream = null;
	}
	video.srcObject = null;
};

const setupCamera = async () => {
	try {
		_stream = await navigator.mediaDevices.getUserMedia({
			video: {
				width: { min: 800, ideal: 1280 },
				frameRate: { ideal: 60 },
			},
			audio: false,
		});
		const videoTrack = _stream.getVideoTracks()[0];
		const { width, height } = videoTrack.getSettings();

		video.width = width;
		video.height = height;
		cameraCanvas.width = width;
		cameraCanvas.height = height;
		cameraAspectRatio = width / height;
		cameraSize[0] = width;
		cameraSize[1] = height;

		video.srcObject = _stream;
		video.play();

		_isDrawing = true;
		drawToCanvas();
	} catch (e) {
		_isDrawing = false;
		if (_stream) {
			_stream.getTracks().forEach(track => track.stop());
			_stream = null;
		}
		console.warn(`Camera not initialized: ${e}`);
	}
};

export { cameraCanvas, cameraAspectRatio, cameraSize, setupCamera, stopCameraLoop, stopCamera };
