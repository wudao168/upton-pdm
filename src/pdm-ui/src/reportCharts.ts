export interface ReportChartDatum { key:string;label:string;value:number|null;color?:string;hint?:string;group?:string }
export interface ReportChartSeries { label:string;color:string;values:(number|null)[];hints?:string[] }
export const chartNumber=(value:number)=>Math.abs(value)>=1e8?(value/1e8).toFixed(1)+'亿':Math.abs(value)>=1e4?(value/1e4).toFixed(1)+'万':Number.isInteger(value)?String(value):value.toFixed(1)
export function chartMaximum(values:(number|null)[],percentage=false){if(percentage)return 100;const max=Math.max(0,...values.filter((v):v is number=>v!=null&&Number.isFinite(v)));if(max===0)return 1;const power=10**Math.floor(Math.log10(max));return Math.ceil(max/power*2)/2*power}
// 缺失观测切断曲线，不以零补齐，也不跨越缺失月份连线。
export function chartSegments(values:(number|null)[],x:(i:number)=>number,y:(v:number)=>number){const paths:string[]=[];let path='';values.forEach((v,i)=>{if(v==null||!Number.isFinite(v)){if(path)paths.push(path);path=''}else path+=(path?' L':'M')+x(i)+','+y(v)});if(path)paths.push(path);return paths}
export function cumulativePercent(values:(number|null)[],overall?:number){const total=Math.max(overall??0,values.reduce<number>((s,v)=>s+Math.max(0,v??0),0));let running=0;return values.map(v=>total?(running+=Math.max(0,v??0))/total*100:null)}
