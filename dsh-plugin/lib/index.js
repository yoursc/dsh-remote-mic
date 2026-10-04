/**
 * Host 侧入口 —— 第 1 步是**空的**。
 *
 * v1 的 Host 部分不需要任何东西：转写能力由 `@deepseek-ai/dsh-experimental-voice-input-bundle`
 * 挂好的 `remote.speech` 提供（见 docs/DSH-SEAMS.md §7.1），我们只是客户端消费者。
 *
 * 但这个文件必须存在：cordis.patch.yml 插入的那一行会让 loader 去加载本包的 `main`，
 * 没有它整棵插件树在启动时就挂掉。导出一个什么都不做的 apply 即可。
 */
export function apply() {
  // 故意为空。将来若要做 v2 的审批 seam（AGENTS.md 定案 A3 之外的路），Host 部分从这里长出来。
}