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
