/**
 * 文件：developerContact.ts
 * 用途：从公开运行时配置中读取开发者联系方式。
 * 核心职责：把微信号和二维码地址从页面代码中解耦，部署后可直接替换配置而无需重新构建前端。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-25
 */

export type DeveloperContact = {
  weChatId: string
  qrCodeUrl: string
}

type ContactConfigSource = Partial<Record<keyof DeveloperContact, unknown>>

/**
 * 禁用浏览器缓存，保证管理员替换 contact-config.json 后，用户刷新即可读取最新联系方式。
 */
export async function loadDeveloperContact(): Promise<DeveloperContact> {
  const controller = new AbortController()
  const timeout = window.setTimeout(() => controller.abort(), 8000)
  let response: Response
  try {
    // 联系方式与主接口并行加载；静态服务器异常时也必须在有限时间内结束，不能卡住工作台初始化。
    response = await fetch('/contact-config.json', { cache: 'no-store', signal: controller.signal })
  } catch (reason) {
    if (controller.signal.aborted) throw new Error('联系方式配置加载超时')
    throw reason
  } finally {
    window.clearTimeout(timeout)
  }
  if (!response.ok) throw new Error(`联系方式配置加载失败（${response.status}）`)

  const source = await response.json() as ContactConfigSource
  return {
    weChatId: typeof source.weChatId === 'string' ? source.weChatId.trim() : '',
    qrCodeUrl: typeof source.qrCodeUrl === 'string' ? source.qrCodeUrl.trim() : '',
  }
}
