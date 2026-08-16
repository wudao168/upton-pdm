<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  isTyping?: boolean
  showPassword?: boolean
  passwordLength?: number
}>(), {
  isTyping: false,
  showPassword: false,
  passwordLength: 0,
})

const characterState = computed(() => {
  if (props.passwordLength > 0 && props.showPassword) return 'showing-password'
  if (props.passwordLength > 0) return 'hiding-password'
  if (props.isTyping) return 'typing'
  return 'idle'
})
</script>

<template>
  <div class="pdm-login-characters" :data-state="characterState" aria-hidden="true">
    <div class="pdm-login-character is-purple"><span class="pdm-character-eyes"><i /><i /></span></div>
    <div class="pdm-login-character is-charcoal"><span class="pdm-character-eyes"><i /><i /></span></div>
    <div class="pdm-login-character is-orange"><span class="pdm-character-eyes"><i /><i /></span></div>
    <div class="pdm-login-character is-yellow">
      <span class="pdm-character-eyes"><i /><i /></span>
      <span class="pdm-character-mouth" />
    </div>
  </div>
</template>

<style scoped>
.pdm-login-characters {
  position: relative;
  width: 550px;
  height: 440px;
}

.pdm-login-character {
  position: absolute;
  bottom: 0;
  transform-origin: bottom center;
  transition: height 500ms ease, transform 500ms ease;
}

.is-purple { left: 70px; z-index: 1; width: 180px; height: 400px; border-radius: 10px 10px 0 0; background: #6c3ff5; }
.is-charcoal { left: 240px; z-index: 2; width: 120px; height: 310px; border-radius: 8px 8px 0 0; background: #2d2d2d; }
.is-orange { left: 0; z-index: 3; width: 240px; height: 200px; border-radius: 120px 120px 0 0; background: #ff9b6b; }
.is-yellow { left: 310px; z-index: 4; width: 140px; height: 230px; border-radius: 70px 70px 0 0; background: #e8d754; }

.pdm-character-eyes {
  position: absolute;
  display: flex;
  gap: 30px;
  transition: transform 300ms ease;
}

.pdm-character-eyes i {
  display: block;
  width: 16px;
  height: 16px;
  border: 5px solid #fff;
  border-radius: 50%;
  background: #2d2d2d;
}

.is-purple .pdm-character-eyes { top: 42px; left: 45px; }
.is-charcoal .pdm-character-eyes { top: 34px; left: 27px; gap: 22px; }
.is-charcoal .pdm-character-eyes i { width: 15px; height: 15px; border-width: 4px; }
.is-orange .pdm-character-eyes { top: 90px; left: 82px; }
.is-orange .pdm-character-eyes i,
.is-yellow .pdm-character-eyes i { width: 12px; height: 12px; border: 0; }
.is-yellow .pdm-character-eyes { top: 40px; left: 52px; gap: 24px; }

.pdm-character-mouth {
  position: absolute;
  top: 88px;
  left: 40px;
  width: 80px;
  height: 4px;
  border-radius: 999px;
  background: #2d2d2d;
  transition: transform 300ms ease;
}

[data-state="typing"] .is-purple,
[data-state="hiding-password"] .is-purple { height: 440px; transform: translateX(35px) skewX(-7deg); }
[data-state="typing"] .is-charcoal,
[data-state="hiding-password"] .is-charcoal { transform: skewX(-5deg); }
[data-state="hiding-password"] .pdm-character-eyes { transform: translate(-8px, -3px); }
[data-state="showing-password"] .pdm-character-eyes { transform: translate(-10px, -6px); }
[data-state="showing-password"] .pdm-character-mouth { transform: translateX(-12px); }

@media (prefers-reduced-motion: reduce) {
  .pdm-login-character,
  .pdm-character-eyes,
  .pdm-character-mouth { transition: none; }
}
</style>
