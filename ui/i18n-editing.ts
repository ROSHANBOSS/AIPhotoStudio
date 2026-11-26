import {manual} from './i18n-editing-manual';
import {portrait} from './i18n-editing-portrait';
import {masks} from './i18n-editing-masks';
import {native} from './i18n-editing-native';
import {errors} from './i18n-editing-errors';
import {canvas} from './i18n-editing-canvas';

export const strings: Record<string,string> = {
  ...manual, ...masks, ...portrait, ...native, ...errors, ...canvas,
  '青色': 'Aqua',
  '曝光': 'Exposure',
  '明度': 'Luminance',
  '色相（°）': 'Hue (°)',
  '自动美型': 'Automatic reshaping',
  '仅调整所选的人物': 'Adjust only the selected person',
  '选择画面中的人物': 'Select a person in the photo',
  '皮肤': 'Skin',
  '脸': 'Face',
};

// Native controls transmit group and channel as a single label. Build complete
// English labels so number fields, history and accessible names agree.
for(const channel of ['红','绿','蓝','RGB']){
  for(const value of [25,50,75])strings[channel+' · '+value+'%']=(strings[channel]??channel)+' · '+value+'%';
}
for(const channel of ['红色','橙色','黄色','绿色','青色','蓝色','紫色','洋红','阴影','中间调','高光']){
  for(const property of ['色相（°）','饱和度','着色强度','明度'])
    strings[channel+' · '+property]=strings[channel]+' · '+strings[property];
}
for(const primary of ['红','绿','蓝'])for(const property of ['色相（°）','饱和度'])
  strings[primary+'原色 · '+property]=strings[primary]+' primary · '+strings[property];

const label=(name:string)=>strings[name]??name;
function region(name:string):string {
  if(strings[name])return strings[name];
  const resource=/^(蒙版资源|人体资源|分区资源)\s*(\d+)$/.exec(name);
  if(resource)return ({'蒙版资源':'Mask asset','人体资源':'Body asset','分区资源':'Segmentation asset'} as Record<string,string>)[resource[1]]+' '+resource[2];
  const numbered=/^(人物|人脸|手绘蒙版|线性渐变|径向渐变|颜色范围|亮度范围|抠图背景|虚化背景|背景布|头发|衣服|上装|下装|镜片反光|精细衣料|脸颈与手臂)\s*(\d+)$/.exec(name);
  if(numbered)return label(numbered[1])+' '+numbered[2];
  const person=/^(人物|人脸)\s*(\d+)\s*·\s*(.+)$/.exec(name);
  return person?label(person[1])+' '+person[2]+' · '+region(person[3]):name;
}
const plural=(n:string,word:string)=>n+' '+word+(n==='1'?'':'s');
const changes=(rows:string)=>rows.split('，').map(row=>row.replace(/^(.+?)\s+([+−\d.-]+)\s*→\s*([+−\d.-]+)$/,
  (_match,control:string,from:string,to:string)=>region(control)+' '+from+' → '+to)).join(', ');
export const patterns: Array<[RegExp,(...groups:string[])=>string]> = [
  [/^(.+\s+[+−\d.-]+\s*→\s*[+−\d.-]+)$/, changes],
  [/^(.+?)(参数选项|选项|数值|的锁定与历史)$/, (control,suffix)=>region(control)+' '+label(suffix)],
  [/^还原人物\s*(\d+)\s*的美颜$/, n=>'Reset retouching for person '+n],
  [/^选择(红色|橙色|黄色|绿色|青色|蓝色|紫色|洋红)通道$/, c=>'Select '+label(c).toLowerCase()+' channel'],
  [/^(阴影|中间调|高光)色轮，可拖动或使用下方滑块$/, z=>label(z)+' color wheel. Drag on the wheel or use the sliders below.'],
  [/^调整(阴影|中间调|高光)色轮$/, z=>'Adjust '+label(z).toLowerCase()+' color wheel'],
  [/^调整(.+)曲线$/, c=>'Adjust '+region(c)+' curve'],
  [/^调整(.+)$/, c=>'Adjust '+region(c)],
  [/^(人物|人脸)\s*(\d+)\s*·\s*(.+)$/, (type,n,item)=>label(type)+' '+n+' · '+region(item)],
  [/^(蒙版资源|人体资源|分区资源)\s*(\d+)$/, (type,n)=>region(type+' '+n)],
  [/^(人物|人脸|原人脸|原人体|候选点|保护框|遮挡|补保护|擦保护|手绘蒙版|线性渐变|径向渐变|颜色范围|亮度范围|背景布|头发|上装|下装|衣服|精细衣料|镜片反光|脸颈与手臂)\s*(\d+)$/, (type,n)=>label(type)+' '+n],
  [/^人物\s*(\d+)（需定位身体）$/, n=>'Person '+n+' (body detection needed)'],
  [/^选择画面中的(人物|人脸)\s*(\d+)$/, (type,n)=>'Select '+label(type).toLowerCase()+' '+n+' in the photo'],
  [/^(恢复人物|移除保留点|遮挡画面中的人脸)\s*(\d+)$/, (s,n)=>label(s)+' '+n],
  [/^已忽略\s*(\d+)\s*人$/, n=>n+(n==='1'?' person':' people')+' ignored'],
  [/^人物\s*(\d+)\s*尚未可靠定位(人脸|身体)。可框选这位人物补检。$/, (n,part)=>'The '+label(part).toLowerCase()+' of person '+n+' could not be reliably located. Draw a box to detect again.'],
  [/^已定位\s*(\d+)\s*人的身体。点画面中的人物框或下方编号，单独调整。$/, n=>'Located '+n+' people. Select a box or a number below to adjust one person.'],
  [/^检测到\s*(\d+)\s*张脸。点照片上的框或编号，分别调整。$/, n=>'Detected '+plural(n,'face')+'. Select a box or a number to adjust a face.'],
  [/^从左数第\s*([\d、 ]+)\s*张脸的肩躯定位不足，无法单独进行人体美型。$/, n=>'Face '+n.replace(/、/g,', ')+' from the left has insufficient shoulder and torso detail for reshaping.'],
  [/^当前选择人物\s*(\d+)。$/, n=>'Selected person: '+n+'.'],
  [/^当前选择人物\s*(\d+)。(可见部位不足，请选择其他人物。|仅显示能可靠定位的部位。|可按部位微调。)$/, (n,detail)=>'Selected person: '+n+'. '+label(detail)],
  [/^(人像|人体美型)\s*·\s*(人物|原人脸|原人体)\s*(\d+)(（定位时恢复显示）|\s*·\s*部位未定位)?$/, (group,type,n,status)=>label(group)+' · '+label(type)+' '+n+(status?' '+label(status.trim()):'')],
  [/^原蒙版\s*(\d+)\s*·\s*无法定位$/, n=>'Original mask '+n+' · Unavailable'],
  [/^照片中识别到超过\s*(\d+)\s*个人物。请选用人数较少的裁切副本继续美型。$/, n=>'More than '+n+' people were detected. Use a cropped copy containing fewer people.'],
  [/^识别到超过\s*(\d+)\s*张脸，请使用包含目标人物的裁切副本。$/, n=>'More than '+n+' faces were detected. Use a cropped copy containing the desired person.'],
  [/^已找到\s*(\d+)\s*处局部色差。请点选想修复的编号，保留痣、雀斑和正常纹理。已选\s*(\d+)\s*处。$/, (total,n)=>'Found '+total+' potential blemish'+(total==='1'?'':'es')+'. Select spots to repair, leaving moles, freckles and natural texture intact. Selected: '+n+'.'],
  [/^已标记\s*(\d+)\s*处$/, n=>plural(n,'spot')+' marked'],
  [/^人脸短边的\s*([\d.]+)%$/, n=>n+'% of the face’s shorter dimension'],
  [/^(待保存|已保存|无覆盖)\s*·\s*(\d+)\s*笔$/, (status,n)=>label(status)+' · '+plural(n,'stroke')],
  [/^未找到可靠的(.+?)区域，可用画笔手动选择。$/, item=>'Could not reliably locate '+region(item).toLowerCase()+'. Select the area manually with the brush.'],
  [/^正在本机识别(头发|衣服|人物肤色|背景)…?$/, s=>'Detecting '+label(s).toLowerCase()+' on this device…'],
  [/^拖出新框，替换第\s*(\d+)\s*处(遮挡|保护范围)。$/, (n,type)=>'Drag to replace '+label(type).toLowerCase()+' '+n+'.'],
  [/^移除(遮挡|保护框|补保护|擦保护)\s*(\d+)$/, (type,n)=>'Remove '+label(type).toLowerCase()+' '+n],
  [/^(渐变起点|渐变终点|椭圆中心|椭圆横半径|椭圆纵半径)\s*·\s*方向键微调$/, item=>label(item)+' · Fine-tune with the arrow keys'],
  [/^(新建蒙版|编辑渐变|编辑范围|重命名蒙版|移除蒙版|导入 LUT)\s*·\s*(.+)$/, (action,name)=>(strings[action+' ·']??label(action))+' '+region(name)],
  [/^点纸面的(左上角|右上角|右下角|左下角)$/, c=>'Click the '+label(c).toLowerCase()+' corner of the page'],
  [/^已对齐(垂直|水平)方向，可预览自动裁边。$/, d=>'Aligned '+(d==='水平'?'horizontally':'vertically')+'. Preview the automatic crop.'],
  [/^(左上|右上|左下|右下)(垂直内移|水平内移)$/, (c,d)=>label(c)+' '+label(d)],
  [/^(\d+)\s*×\s*(\d+)\s*像素$/, (w,h)=>w+' × '+h+' px'],
  [/^第\s*(\d+)\s*轨$/, n=>'Track '+n],
  [/^每张照片支持 1 至\s*(\d+)\s*位人物。$/, n=>'Each photo supports between 1 and '+n+' people.'],
  [/^(\d+)\s*项已调整$/, n=>plural(n,'control')+' changed'],
  [/^预览候选\s*([AB])$/, n=>'Preview sample '+n],
  [/^候选\s*([AB])\s*取样放大$/, n=>'Magnified sample '+n],
  [/^候选\s*([AB])$/, n=>'Sample '+n],
  [/^点照片为\s*([AB])\s*取样。A、B 都从已保存的画面取色，方框标出取样像素。$/, n=>'Click the photo to take sample '+n+'. A and B both sample the saved image. The square marks the sampled pixels.'],
  [/^色温\s*([+\d.−-]+)\s*·\s*色调\s*([+\d.−-]+)$/, (t,i)=>'Temperature '+t+' · Tint '+i],
  [/^(色温|色调|色温、色调)已锁定$/, k=>k.split('、').map(label).join(' and ')+' locked'],
  [/^全图(.+)通道\s*·\s*此点影响\s*([\d.]+)%$/, (c,n)=>label(c)+' channel · '+n+'% influence at this point'],
  [/^抽到的\s*([\d,]+)\s*个可见点中，([\d,]+)\s*个（(.+?)）超出 sRGB。这些颜色进入当前编辑空间时会受限，另存为 Adobe RGB 也无法恢复。$/, (total,n,share)=>n+' of '+total+' visible samples ('+label(share)+') fall outside sRGB. Editing in sRGB limits these colors. Exporting to Adobe RGB cannot restore them.'],
  [/^抽到的\s*([\d,]+)\s*个可见点均在 sRGB 范围内。少量局部颜色可能没有被抽到。$/, n=>'All '+n+' visible samples fall within sRGB. Small patches of color may have been missed.'],
];
