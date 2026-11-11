export function samplePdf(): string {
  const stream = '0 0 0 rg 20 20 80 80 re f\n'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}endstream`]
  let value='%PDF-1.4\n'
  const offsets=[0]
  objects.forEach((object,index)=>{offsets.push(value.length);value+=`${index+1} 0 obj\n${object}\nendobj\n`})
  const xref=value.length
  return value+`xref\n0 5\n0000000000 65535 f \n${offsets.slice(1).map(offset=>`${String(offset).padStart(10,'0')} 00000 n \n`).join('')}trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`
}
