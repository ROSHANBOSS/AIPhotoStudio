import {localize} from './locale';
import type {Control} from './model';
import {groupName} from './model';

const everyday: Record<string, string[]> = {
  temperature: ['tooyellow','toowarm','toocold','tooblue','toocool','whitebalance','偏黄','太黄','发黄','偏蓝','太蓝','发蓝','太冷','太暖','冷一点','暖一点','白平衡'],
  tint: ['toogreen','toopink','toopurple','偏绿','发绿','偏紫','发紫','偏粉'],
  exposure: ['toodark','toobright','underexposed','overexposed','太暗','太黑','太亮','曝光不足','曝光过度','亮一点','暗一点','提亮','压暗'],
  highlights: ['brightsky','blownhighlights','过曝','白成一片','天空太亮','亮部太亮','高光太亮'],
  shadows: ['darkshadows','shadowsaretoodark','暗部','阴影太黑','黑成一片','逆光太暗','看不清暗处'],
  contrast: ['flat','washedout','发灰','太灰','灰蒙蒙','没层次','反差大','反差小'],
  dehaze: ['发灰','太灰','灰蒙蒙','有雾','雾蒙蒙','去雾气'],
  saturation: ['太艳','太鲜艳','颜色太浓','颜色太淡','掉色','鲜艳一点'],
  vibrance: ['颜色太淡','没气色','不够鲜艳'],
  sharpness: ['不够清楚','有点糊','太糊','不清晰'],
  noise: ['noisy','grainy','噪点','颗粒太多','画面很脏','噪声'],
  noise_chroma: ['彩噪','红绿杂点','夜景彩斑','色度噪点'],
  noise_luma: ['灰色颗粒','明暗噪点','亮度噪点','夜景颗粒'],
};

const normalize = (value: string) => value.normalize('NFKC').toLowerCase().replace(/[\s，。！？、,.!?]+/g, '');

export function parameterSearch(query: string) {
  const term = normalize(query);
  const faceLight = /darkface|faceistoodark|brightenface/.test(term)||/(?:脸|人像|皮肤|肤色|人脸|脸部|面部)(?:看起来|显得|有点|太|很|有些|偏)?(?:暗|黑)|(?:提亮|调亮)(?:一下|一点)?(?:人脸|脸|肤色)|(?:人脸|脸部|皮肤|肤色)提亮/.test(term);
  const keys = new Set(Object.keys(everyday).filter(key => everyday[key].some(word => term.includes(word))));
  return {
    faceLight,
    matches: (c: Control, group: string) => !term ||
      normalize(c.label+' '+c.key+' '+groupName(group)+' '+localize(c.label)+' '+localize(groupName(group))).includes(term) || normalize(c.group).includes(term) ||
      !faceLight && keys.has(c.key),
  };
}
