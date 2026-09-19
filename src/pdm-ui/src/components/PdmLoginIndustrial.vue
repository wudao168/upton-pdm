<!-- 登录页工业风背景：蓝图网格 + 齿轮 + 机械臂 + 输送线 + 尺寸标注。纯矢量渲染，不依赖外部图片。 -->
<template>
  <svg class="pdm-industrial" viewBox="0 0 1200 800" preserveAspectRatio="xMidYMid slice" aria-hidden="true" focusable="false">
    <defs>
      <pattern id="pdmIndustrialGrid" width="40" height="40" patternUnits="userSpaceOnUse">
        <path d="M40 0H0V40" fill="none" stroke="#ffffff" stroke-opacity="0.05" stroke-width="1" />
      </pattern>
      <pattern id="pdmIndustrialGridLarge" width="200" height="200" patternUnits="userSpaceOnUse">
        <rect width="200" height="200" fill="url(#pdmIndustrialGrid)" />
        <path d="M200 0H0V200" fill="none" stroke="#ffffff" stroke-opacity="0.1" stroke-width="1.5" />
      </pattern>
    </defs>

    <rect width="1200" height="800" fill="url(#pdmIndustrialGridLarge)" />

    <g stroke="#9fd4ff" stroke-opacity="0.28" fill="none" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
      <!-- 齿轮组 -->
      <g class="pdm-industrial-gear" style="transform-origin: 235px 250px">
        <circle cx="235" cy="250" r="86" />
        <circle cx="235" cy="250" r="34" />
        <path d="M235 148v-22M235 374v-22M133 250h-22M359 250h-22M163 178l-16-16M307 322l-16-16M307 178l16-16M163 322l-16-16" />
      </g>
      <g class="pdm-industrial-gear is-reverse" style="transform-origin: 392px 352px">
        <circle cx="392" cy="352" r="54" />
        <circle cx="392" cy="352" r="20" />
        <path d="M392 288v-14M392 430v-14M328 352h-14M470 352h-14M347 307l-10-10M447 407l-10-10M437 307l10-10M347 397l-10-10" />
      </g>

      <!-- 机械臂 -->
      <g>
        <rect x="880" y="520" width="120" height="26" rx="6" />
        <path d="M918 520V438l84-96" />
        <path d="M1002 342l52 34-24 44-52-34z" />
        <path d="M1054 376l58 22" />
        <circle cx="918" cy="438" r="12" />
        <circle cx="1002" cy="342" r="12" />
      </g>

      <!-- 输送线与工件 -->
      <g>
        <path d="M120 690h960" />
        <path d="M120 726h960" />
        <g class="pdm-industrial-conveyor-track">
          <rect x="150" y="640" width="86" height="50" rx="6" />
          <rect x="330" y="640" width="86" height="50" rx="6" />
          <rect x="510" y="640" width="86" height="50" rx="6" />
          <rect x="690" y="640" width="86" height="50" rx="6" />
          <rect x="870" y="640" width="86" height="50" rx="6" />
        </g>
        <path d="M150 726v22M330 726v22M510 726v22M690 726v22M870 726v22" stroke-opacity="0.16" />
      </g>

      <!-- 尺寸标注 -->
      <g stroke-opacity="0.2" stroke-width="2">
        <path d="M150 592h836" />
        <path d="M150 580v24M986 580v24M986 586l-14 6 14 6M150 586l14 6-14 6" />
        <path d="M420 96v96M406 96h28M406 192h28" />
      </g>

      <!-- 管道 -->
      <path d="M60 140h180a28 28 0 0 1 28 28v96" stroke-opacity="0.18" />
      <path d="M1100 210h-70a26 26 0 0 0-26 26v140" stroke-opacity="0.18" />
    </g>
  </svg>
</template>
