import messages from './i18n-native-messages.json';

export const strings:Record<string,string>=messages;
export const patterns:Array<[RegExp,(...groups:string[])=>string]>=[
  [/^已确认服务可访问，模型 (.+) 可见。$/,model=>'The service is reachable and model '+model+' is available.'],
];
