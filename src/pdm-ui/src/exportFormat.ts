import { strToU8, strFromU8, unzipSync, zipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { exportCompanyLogo } from './exportCompanyLogo'
export type ProjectExportMetadata = { title: string; company: string; scope: string; versionLabel: string; version: string; exportedAt: string; exportedBy: string; notes?:string; fields: Array<{key:string;value:string}> }
const ns='http://schemas.openxmlformats.org/spreadsheetml/2006/main', rel='http://schemas.openxmlformats.org/package/2006/relationships', docRel='http://schemas.openxmlformats.org/officeDocument/2006/relationships'
const xml=(v:string)=>v.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')
const column=(index:number)=>{let value='';for(let n=index+1;n>0;n=Math.floor((n-1)/26))value=String.fromCharCode(65+(n-1)%26)+value;return value}
const colIndex=(value:string)=>[...value].reduce((n,c)=>n*26+c.charCodeAt(0)-64,0)
export function projectHeaderHtml(meta: ProjectExportMetadata) {
  return `<header class="uplm-export-header"><div class="uplm-export-brand"><small>${xml(meta.company)}</small><h1>${xml(meta.title)}</h1><img src="data:image/png;base64,${exportCompanyLogo}" alt="公司LOGO"></div><div class="uplm-export-scope">范围：${xml(meta.scope)}　${xml(meta.versionLabel)}：${xml(meta.version)}　导出日期：${xml(meta.exportedAt.replace('T',' ').slice(0,16))}${meta.notes ? `<br>${xml(meta.notes)}` : ''}</div><div class="uplm-export-fields">${meta.fields.map(f=>`<div><b>${xml(f.key)}</b><span>${xml(f.value||'—')}</span></div>`).join('')}</div></header>`
}
export const projectHeaderCss='.uplm-export-brand{display:grid;grid-template-columns:1fr 1.4fr 1fr;align-items:center;border-bottom:2px solid #243e66;padding:0 0 10px}.uplm-export-brand h1{text-align:center;margin:0;font-size:18px}.uplm-export-brand img{width:145px;justify-self:end}.uplm-export-scope{font-size:11px;padding:8px 0}.uplm-export-fields{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));border-top:1px solid #bbc9da;border-left:1px solid #bbc9da;margin-bottom:12px}.uplm-export-fields>div{display:flex;border-bottom:1px solid #bbc9da;border-right:1px solid #bbc9da;min-height:28px;font-size:11px}.uplm-export-fields b{font-weight:500;background:#f1f5fa;width:74px;flex:0 0 74px;padding:6px}.uplm-export-fields span{padding:6px;overflow-wrap:anywhere}.uplm-export-section{break-before:page}.uplm-export-section:first-child{break-before:auto}'
export function formatProjectWorkbook(bytes:Uint8Array, metadata:ProjectExportMetadata[], removeRows:number[] = []) {
  const files=unzipSync(bytes), parser=new DOMParser(), serializer=new XMLSerializer()
  const load=(path:string,fallback='')=>parser.parseFromString(files[path]?strFromU8(files[path]!):fallback,'application/xml')
  const save=(path:string,doc:Document)=>{files[path]=strToU8(serializer.serializeToString(doc))}
  const styles=load('xl/styles.xml',`<styleSheet xmlns="${ns}"><fonts count="1"><font><sz val="11"/><name val="Microsoft YaHei"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs></styleSheet>`)
  function add(collection:string,content:string) {const parent=styles.getElementsByTagName(collection)[0]!;const index=parent.children.length;parent.appendChild(styles.importNode(parser.parseFromString(content,'application/xml').documentElement,true));parent.setAttribute('count',String(index+1));return index}
  const font=add('fonts',`<font xmlns="${ns}"><sz val="11"/><color rgb="FF203149"/><name val="Microsoft YaHei"/></font>`),titleFont=add('fonts',`<font xmlns="${ns}"><b/><sz val="18"/><name val="Microsoft YaHei"/></font>`)
  const fill=add('fills',`<fill xmlns="${ns}"><patternFill patternType="solid"><fgColor rgb="FFF1F5FA"/><bgColor indexed="64"/></patternFill></fill>`)
  const border=add('borders',`<border xmlns="${ns}">${['left','right','top','bottom'].map(s=>`<${s} style="thin"><color rgb="FFBBC9DA"/></${s}>`).join('')}</border>`)
  const style=(f:number,bg:number,b:number,align:string)=>add('cellXfs',`<xf xmlns="${ns}" numFmtId="0" fontId="${f}" fillId="${bg}" borderId="${b}" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="${align}" vertical="center" wrapText="1"/></xf>`)
  const titleStyle=style(titleFont,0,0,'center'),valueStyle=style(font,0,border,'left'),labelStyle=style(font,fill,border,'left'),infoStyle=style(font,0,0,'left'),tableHeaderStyle=style(font,fill,border,'center')
  save('xl/styles.xml',styles)
  const types=load('[Content_Types].xml'), typeNs=types.documentElement.namespaceURI!
  function override(path:string,type:string){if([...types.documentElement.children].some(e=>e.getAttribute('PartName')==='/'+path))return;const element=types.createElementNS(typeNs,'Override');element.setAttribute('PartName','/'+path);element.setAttribute('ContentType',type);types.documentElement.appendChild(element)}
  override('xl/styles.xml','application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml')
  const bookRels=load('xl/_rels/workbook.xml.rels',`<Relationships xmlns="${rel}"/>`)
  if(![...bookRels.documentElement.children].some(e=>e.getAttribute('Type')===docRel+'/styles')){const e=bookRels.createElementNS(rel,'Relationship');e.setAttribute('Id','uplmStyles');e.setAttribute('Type',docRel+'/styles');e.setAttribute('Target','styles.xml');bookRels.documentElement.appendChild(e)}save('xl/_rels/workbook.xml.rels',bookRels)
  files['xl/media/uplm-company-logo.png']=Uint8Array.from(atob(exportCompanyLogo),c=>c.charCodeAt(0))
  if(![...types.documentElement.children].some(e=>e.getAttribute('Extension')==='png')){const e=types.createElementNS(typeNs,'Default');e.setAttribute('Extension','png');e.setAttribute('ContentType','image/png');types.documentElement.insertBefore(e,types.documentElement.firstChild)}
  const book=load('xl/workbook.xml'), bookSheets=[...book.getElementsByTagName('sheet')]
  let defined=book.getElementsByTagName('definedNames')[0];if(!defined){defined=book.createElementNS(ns,'definedNames');book.documentElement.insertBefore(defined,book.getElementsByTagName('sheets')[0]!.nextSibling)}
  const paths=Object.keys(files).filter(p=>/^xl\/worksheets\/sheet\d+\.xml$/.test(p)).sort((a,b)=>Number(a.match(/sheet(\d+)/)![1])-Number(b.match(/sheet(\d+)/)![1]))
  paths.forEach((path,index)=>{
    if(bookSheets[index]?.getAttribute('state')==='hidden')return
    const meta=metadata[index]??metadata[0]!;if(!meta)return
    const source = strFromU8(files[path]!), originalData = source.match(/<sheetData>([\s\S]*?)<\/sheetData>/)?.[1] ?? ''
    const sheet=parser.parseFromString(source.replace(/<sheetData>[\s\S]*?<\/sheetData>/,'<sheetData/>'),'application/xml'),root=sheet.documentElement,data=sheet.getElementsByTagName('sheetData')[0]!,remove=removeRows[index]??0,delta=6-remove
    let shiftedData = originalData.replace(/<row\b[^>]*>[\s\S]*?<\/row>/g, row => {
      const number=Number(row.match(/\br="(\d+)"/)?.[1]); if(number<=remove)return ''
      const shifted=row.replace(/\br="([^"]+)"/g,(_,value)=>`r="${/^\d+$/.test(value)?Number(value)+delta:value.replace(/([A-Z]+)(\d+)/g,(_:string,c:string,n:string)=>c+(Number(n)+delta))}"`)
      let next=0
      return shifted.replace(/<c\b([^>]*)>/g,(_,attrs)=>{const reference=attrs.match(/\br="([A-Z]+)\d+"/);if(reference){next=colIndex(reference[1]);return `<c${attrs}>`}return `<c r="${column(next++)}${number+delta}"${attrs}>`})
    })
    const element=(name:string,attrs:Record<string,string>={})=>{const e=sheet.createElementNS(ns,name);Object.entries(attrs).forEach(([k,v])=>e.setAttribute(k,v));return e}
    ;[...data.children].filter(r=>Number(r.getAttribute('r'))<=remove).forEach(r=>r.remove())
    ;[...sheet.getElementsByTagName('mergeCell')].filter(e=>Number(e.getAttribute('ref')?.match(/\d+/)?.[0])<=remove).forEach(e=>e.remove())
    const shift=(v:string)=>v.replace(/(\$?[A-Z]+\$?)(\d+)/g,(_,c,n)=>c+(Number(n)+delta))
    ;[...root.querySelectorAll('*')].forEach(e=>{[...e.attributes].forEach(a=>{if(a.name==='r'&&e.localName==='row')a.value=String(Number(a.value)+delta);else if(['r','ref','sqref','topLeftCell','activeCell'].includes(a.name))a.value=shift(a.value)})})
    const dataColumns=Math.max(...[...shiftedData.matchAll(/<c\b[^>]*\br="([A-Z]+)\d+"/g)].map(match=>colIndex(match[1]!)),1)
    const count=Math.max(8,dataColumns)
    let merges=sheet.getElementsByTagName('mergeCells')[0];if(!merges){merges=element('mergeCells');root.appendChild(merges)}
    const merge=(reference:string)=>merges!.appendChild(element('mergeCell',{ref:reference}))
    const cell=(r:number,c:number,value:string,s:number)=>{const e=element('c',{r:column(c)+r,s:String(s),t:'inlineStr'}),is=element('is'),t=element('t');t.textContent=value;is.appendChild(t);e.appendChild(is);return e}
    const rows:Element[]=[]
    const row=(r:number,height:number)=>{const e=element('row',{r:String(r),ht:String(height),customHeight:'1'});rows.push(e);return e}
    row(1,32).appendChild(cell(1,0,meta.title,titleStyle));merge(`A1:${column(count-1)}1`)
    row(2,meta.notes?38:24).appendChild(cell(2,0,`${meta.company}　范围：${meta.scope}　${meta.versionLabel}：${meta.version}　导出日期：${meta.exportedAt.replace('T',' ').slice(0,16)}${meta.notes?`\n${meta.notes}`:''}`,infoStyle));merge(`A2:${column(count-1)}2`)
    for(let r=0;r<3;r++){if (!meta.fields[r*4]) { row(r+3,8); continue } const e=row(r+3,28);for(let pair=0;pair<4;pair++){const start=Math.floor(pair*count/4),end=Math.floor((pair+1)*count/4)-1,labelEnd=start+Math.max(1,Math.floor((end-start+1)/4))-1,field=meta.fields[r*4+pair];e.append(cell(r+3,start,field?.key??'',labelStyle),cell(r+3,labelEnd+1,field?.value||'—',valueStyle));if(labelEnd>start)merge(`${column(start)}${r+3}:${column(labelEnd)}${r+3}`);if(end>labelEnd+1)merge(`${column(labelEnd+1)}${r+3}:${column(end)}${r+3}`)}}row(6,8);data.prepend(...rows);merges.setAttribute('count',String(merges.children.length))
    const pane=sheet.getElementsByTagName('pane')[0];const repeat=Math.max(7,Number(pane?.getAttribute('ySplit')??1)+delta);pane?.setAttribute('ySplit',String(repeat))
    if(!remove)shiftedData=shiftedData.replace(/<row\b[^>]*\br="7"[^>]*>[\s\S]*?<\/row>/,row=>row.replace(/<c\b([^>]*)>/g,(_,attrs)=>`<c${attrs.replace(/\s+s="[^"]*"/,'')} s="${tableHeaderStyle}">`))
    data.appendChild(sheet.createComment('uplm-data-rows'))
    if(!remove && !sheet.getElementsByTagName('autoFilter').length){const lastRow=Math.max(...[...shiftedData.matchAll(/<row[^>]*r="(\d+)"/g)].map(m=>Number(m[1])),7);root.appendChild(element('autoFilter',{ref:`A7:`+column(dataColumns-1)+lastRow}))}
    ;['dimension','pageMargins','pageSetup','headerFooter','drawing'].forEach(name=>[...root.children].filter(e=>e.localName===name).forEach(e=>e.remove()))
    let properties=sheet.getElementsByTagName('sheetPr')[0];if(!properties){properties=element('sheetPr');root.insertBefore(properties,root.firstChild)}properties.appendChild(element('pageSetUpPr',{fitToPage:'1'}))
    root.appendChild(element('pageMargins',{left:'0.3',right:'0.3',top:'0.3',bottom:'0.3',header:'0.1',footer:'0.1'}));root.appendChild(element('pageSetup',{orientation:'landscape',paperSize:'9',fitToWidth:'1',fitToHeight:'0'}))
    const footer=element('headerFooter'),odd=element('oddFooter');odd.textContent=`&L导出人：${meta.exportedBy||'—'}　来源：UPLM&R第&P页 / 共&N页`;footer.appendChild(odd);root.appendChild(footer)
    const drawing=`uplm-header${index+1}.xml`,draw=element('drawing');draw.setAttributeNS(docRel,'r:id','uplmHeader');root.appendChild(draw)
    const relPath=`xl/worksheets/_rels/${path.split('/').at(-1)}.rels`,rels=load(relPath,`<Relationships xmlns="${rel}"/>`),link=rels.createElementNS(rel,'Relationship');link.setAttribute('Id','uplmHeader');link.setAttribute('Type',docRel+'/drawing');link.setAttribute('Target','../drawings/'+drawing);rels.documentElement.appendChild(link);save(relPath,rels)
    files['xl/drawings/'+drawing]=strToU8(`<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="${docRel}"><xdr:oneCellAnchor><xdr:from><xdr:col>${count-3}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>80000</xdr:rowOff></xdr:from><xdr:ext cx="1400000" cy="180000"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="1" name="公司LOGO"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed="image"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>`)
    files['xl/drawings/_rels/'+drawing+'.rels']=strToU8(`<Relationships xmlns="${rel}"><Relationship Id="image" Type="${docRel}/image" Target="../media/uplm-company-logo.png"/></Relationships>`);override('xl/drawings/'+drawing,'application/vnd.openxmlformats-officedocument.drawing+xml')
    ;[...defined!.children].filter(e=>e.getAttribute('localSheetId')===String(index)&&e.getAttribute('name')==='_xlnm.Print_Titles').forEach(e=>e.remove());const definition=book.createElementNS(ns,'definedName');definition.setAttribute('name','_xlnm.Print_Titles');definition.setAttribute('localSheetId',String(index));definition.textContent=`'${bookSheets[index]!.getAttribute('name')!.replace(/'/g,"''")}'!$1:$${repeat}`;defined!.appendChild(definition)
    const order='sheetPr dimension sheetViews sheetFormatPr cols sheetData sheetProtection protectedRanges scenarios autoFilter sortState dataConsolidate customSheetViews mergeCells phoneticPr conditionalFormatting dataValidations hyperlinks printOptions pageMargins pageSetup headerFooter rowBreaks colBreaks drawing legacyDrawing legacyDrawingHF picture tableParts extLst'.split(' ');const children=[...root.children].sort((a,b)=>(order.indexOf(a.localName)<0?99:order.indexOf(a.localName))-(order.indexOf(b.localName)<0?99:order.indexOf(b.localName)));root.replaceChildren(...children);files[path]=strToU8(serializer.serializeToString(sheet).replace('<!--uplm-data-rows-->',shiftedData))
  });save('xl/workbook.xml',book);save('[Content_Types].xml',types);return zipSync(Object.fromEntries(Object.entries(files).map(([name, content])=>[name,new Uint8Array(content)])))
}