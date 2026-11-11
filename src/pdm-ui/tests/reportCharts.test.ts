import { describe,expect,it } from 'vitest'
import { chartSegments,chartMaximum,cumulativePercent,chartNumber } from '../src/reportCharts'
describe('报表图表数据边界',()=>{
 it('未知月份切断曲线，真实零值仍保留',()=>{expect(chartSegments([0,null,20,30,null,0],i=>i*10,v=>100-v)).toEqual(['M0,100','M20,80 L30,70','M50,100'])})
 it('无效数据不生成 SVG 非法路径',()=>{expect(chartSegments([null,NaN,Infinity],i=>i,v=>v)).toEqual([]);expect(chartMaximum([null,NaN,Infinity])).toBe(1)})
 it('累计占比保留前六类实际频次且零样本不造百分比',()=>{expect(cumulativePercent([3,1,null])).toEqual([75,100,100]);expect(cumulativePercent([0,null])).toEqual([null,null]);expect(cumulativePercent([3,1],8)).toEqual([37.5,50])})
 it('金额缩写保留量级且百分比坐标固定为百分制',()=>{expect(chartNumber(120000)).toBe('12.0万');expect(chartMaximum([2,5],true)).toBe(100);expect(chartMaximum([27])).toBe(30)})
})
