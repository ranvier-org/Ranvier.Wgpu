// Node tests: the bytes the packing writers lay out equal the bytes of the .NET structs for the same values.
// golden/packing.json is written by tests/Fable.Ranvier.Wgpu.Golden (`npm run golden`) and is the same on
// every machine.
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";

const golden = JSON.parse(fs.readFileSync(new URL("./golden/packing.json", import.meta.url), "utf8"));
const checks = await import("./generated/PackingChecks.js");
const hex = bytes => Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString("hex");

const layouts = [
  ["Vertex3D", "vertices", 40],
  ["Instance3D", "instances", 96],
  ["LineVertex3D", "lineVertices", 32],
  ["DiscInstance3D", "discs", 64],
  ["user uniform slots", "userUniforms", 16],
];

for (const [name, key, size] of layouts) {
  test(`${name} packs to the ${size}-byte .NET layout`, () => {
    const packed = checks[key]();
    assert.equal(packed.length % size, 0);
    assert.equal(hex(packed), golden[key]);
  });
}

test("the single-instance writer packs each Instance3D as the batch writer does", () => {
  const one = checks.instancesOneByOne();
  assert.equal(one.map(hex).join(""), golden.instances);
});

test("Camera3D.viewProjMatrix equals the .NET result bit for bit", () => {
  assert.equal(hex(checks.sceneUniforms().subarray(0, 64)), golden.sceneUniforms.slice(0, 128));
});

test("Vector3.Normalize equals the .NET result bit for bit", () => {
  assert.equal(hex(checks.normalized()), golden.normalized);
});

// The block holds two normalised light directions, so it is equal only when Vector3.Normalize is.
test("the scene uniform block equals the .NET result bit for bit", () => {
  const packed = checks.sceneUniforms();
  assert.equal(packed.length, 176);
  assert.equal(hex(packed), golden.sceneUniforms);
});

test("the packed scene uniform block has the order the shared file lists", () => {
  assert.equal(hex(checks.sceneUniforms()), hex(checks.sceneUniformValues()));
});

test("Camera3D.orthographic and orbitPerspectiveness matrices match .NET bit-for-bit within float32 tolerance", () => {
  const actualBytes = checks.cameraMatrices();
  assert.equal(actualBytes.length, 6 * 16 * 4);
  assert.equal(typeof golden.cameraMatrices, "string", "golden/packing.json must contain cameraMatrices");
  // The first two matrices (axis-aligned orthographic projMatrix and viewProjMatrix) use no trig and match bit-for-bit.
  assert.equal(hex(actualBytes.subarray(0, 128)), golden.cameraMatrices.slice(0, 256));

  const actual = new Float32Array(actualBytes.buffer, actualBytes.byteOffset, actualBytes.byteLength / 4);
  const expectedBuf = Buffer.from(golden.cameraMatrices, "hex");
  const expected = new Float32Array(expectedBuf.buffer, expectedBuf.byteOffset, expectedBuf.byteLength / 4);
  assert.equal(actual.length, expected.length);
  for (let i = 0; i < actual.length; i++) {
    const diff = Math.abs(actual[i] - expected[i]);
    assert.ok(diff <= 1e-6, `matrix element ${i} differs: JS ${actual[i]} vs .NET ${expected[i]} (diff ${diff})`);
  }
});

test("Camera3D.orthographic and orbitPerspectiveness validate arguments and clamp perspectiveness", () => {
  const v = checks.cameraValidation();
  assert.equal(v.orthoFov, -2);
  assert.equal(v.clampedLow, true);
  assert.equal(v.clampedHigh, true);
  assert.equal(v.zeroHalfHeight, true);
  assert.equal(v.zeroDistance, true);
  assert.equal(v.nanHalfHeight, true);
  assert.equal(v.infDistance, true);
  assert.equal(v.zeroFitRadius, true);
  assert.equal(v.nanPerspectiveness, true);
});

test("Camera3D.orthographic and orbitPerspectiveness keep 0 < NearPlane < distance < FarPlane and project Target to center on small-scale scenes", () => {
  const s = checks.smallScaleCamera();
  for (const [label, c, tol] of [
    ["orthographic", s.ortho, 1e-3],
    ["orbitPerspectiveness(1.0)", s.persp, 1e-3],
    ["orbitPerspectiveness(0.0)", s.orthoZeroPersp, 1e-2],
  ]) {
    assert.equal(c.depthValid, true, `${label}: expected 0 < ${c.nearPlane} < ${c.distance} < ${c.farPlane}`);
    assert.equal(c.projected.length, 2, `${label}: expected Vector3.Zero to project to screen`);
    assert.ok(Math.abs(c.projected[0] - 100) < tol, `${label}: expected center X ~ 100, got ${c.projected[0]}`);
    assert.ok(Math.abs(c.projected[1] - 100) < tol, `${label}: expected center Y ~ 100, got ${c.projected[1]}`);
  }
});
