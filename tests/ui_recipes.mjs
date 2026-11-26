import assert from 'node:assert/strict';
import {recipeValues} from '../ui/model.ts';

const control = (key, group, min, max) => ({key, group, min, max, label:key, initial:0, step:1});
const controls = [
  control('exposure','基础',-5,5),control('contrast','基础',-100,100),
  control('grade_shadows_hue','色彩分级',0,360),control('grade_shadows_sat','色彩分级',0,100),
  control('lut_strength','3D LUT',0,100),control('lut_a','LUT 资源',0,65535),control('lut_b','LUT 资源',0,65535),
  control('local_id','局部资源',0,65535),control('local_exposure','局部调整',-5,5),
  control('portrait_face_w','人脸区域',0,1),control('portrait_face_h','人脸区域',0,1),control('portrait_smooth','人像肤质',0,100),
  control('segment_ref0','分割资源',0,65535),control('segment_foreground','前景与背景',-3,3),control('segment_background','前景与背景',-3,3),
];
const initial = Object.fromEntries(controls.map(c=>[c.key,0]));
const doc = (parameters={}, locks=[])=>({parameters:{...initial,...parameters},locks});
const preset = values=>({name:'测试配方',values});

assert.deepEqual(recipeValues([preset({exposure:.28,contrast:12})],controls,doc({},['contrast']),100,false),{exposure:.28});
assert.deepEqual(recipeValues([preset({contrast:10}),preset({contrast:20})],controls,doc({contrast:20}),50,true),{contrast:35});
const polar = recipeValues([preset({grade_shadows_hue:350,grade_shadows_sat:10}),preset({grade_shadows_hue:10,grade_shadows_sat:10})],controls,doc(),100,false);
assert.ok(!polar.grade_shadows_hue || polar.grade_shadows_hue===360);
assert.equal(polar.grade_shadows_sat,20);
const lut = preset({lut_a:21,lut_b:45,lut_strength:80,local_id:400,local_exposure:2,portrait_face_w:.4,portrait_face_h:.6,portrait_smooth:50});
assert.deepEqual(recipeValues([lut],controls,doc({lut_a:21}),50,false),{lut_strength:40,lut_a:21,lut_b:45});
assert.deepEqual(recipeValues([lut],controls,doc({lut_a:21},['lut_a']),50,false),{});
assert.deepEqual(recipeValues([lut],controls,doc(),0,false),{});
assert.equal(recipeValues([lut],controls,doc({local_id:10}),50,false).local_exposure,1);
assert.equal(recipeValues([lut],controls,doc({portrait_face_w:.2,portrait_face_h:.32}),50,false).portrait_smooth,25);
assert.deepEqual(recipeValues([preset({segment_ref0:19,segment_foreground:1,segment_background:-1})],controls,doc({segment_ref0:11}),100,false),{});
console.log('PASS UI preset composition: precision, locks, circular grade hue, atomic LUT identity, document masks and face regions');
