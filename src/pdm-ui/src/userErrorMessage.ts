export function userErrorMessage(message: string) {
  if (/randomUUID.*(?:not a function|undefined|not defined)/i.test(message))
    return '生成校验标识失败，请刷新页面后重试。'
  if (/Failed to fetch|NetworkError|Load failed|Network request failed/i.test(message))
    return '无法连接服务器，请检查网络后重试。'
  if (/is not a function|is not defined|Cannot read properties|Unexpected token|SyntaxError|TypeError|ReferenceError/i.test(message))
    return '页面处理失败，请刷新页面后重试；如仍失败，请联系管理员。'
  if (/Exception\b|SQLSTATE\b|HRESULT\b|\bError:|\bat\s+[A-Za-z_$][\w.$]*\s*\(/i.test(message))
    return '操作未完成，请重试；如仍失败，请联系管理员。'
  if (!/[\u3400-\u9fff]/.test(message))
    return '操作未完成，请重试；如仍失败，请联系管理员。'
  return message
}
