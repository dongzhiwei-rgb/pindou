<!--
文件：frontend/public/icons/studio/README.md
用途：说明拼了个豆概念图标资产的使用边界，避免新增图标回到混用图标库的状态。
版权：@董志伟-联系方式-makabak1204
最后修改：2026-08-28
-->

# 拼了个豆概念图标资产

这组 SVG 是工作台和交互设计稿共用的正式图标产物。每个文件均使用相同的基础几何：

- `viewBox="0 0 24 24"`，图形安全区为 2–22。
- `stroke-width="1.65"`、`stroke-linecap="round"`、`stroke-linejoin="round"`。
- 不在图标文件内写死品牌色；生产界面通过 CSS Mask 继承按钮的 `currentColor`。
- 默认状态为深墨绿色线稿，选中状态使用薄荷绿底和森林绿线稿，主操作状态使用珊瑚红底和白色线稿。
- 图标只表达语义，不添加阴影、渐变、外层卡片或装饰性圆点。

新增图标时先修改 `frontend/src/icons/studioIcons.ts`，再运行 `node scripts/export-studio-icons.mjs` 重新生成本目录、总览图和审查页。
