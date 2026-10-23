// Snapshot of the owner's installed Matrix3D settings, 2026-10-23.
// Native GlyphSize12 -> 100 base columns; 500 lines -> density5.
// Native FallSpeed6 -> .045; ForwardSpeed6 -> (6-60)*.1/8.
// Explicit URL/CLI values override this scene profile.
export default {
  volumetric: true,
  numColumns: 100,
  density: 5,
  fallSpeed: 0.045,
  forwardSpeed: -0.675,
  codeColor: { space: "rgb", values: [128 / 255, 0, 1] },
  cursorColor: { space: "rgb", values: [1, 128 / 255, 1] },
  sidewaysMovement: true,
  randomForwardMovement: true,
  pictureMonochrome: false,
  pictureDelay: 60,
  pictureDuration: 30,
};
