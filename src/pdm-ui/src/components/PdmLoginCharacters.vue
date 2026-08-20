<!--
  Vue port of CareerCompass AnimatedCharacters:
  https://github.com/arsh342/careercompass/blob/main/src/components/ui/animated-characters.tsx
  Copyright (c) 2025 arsh342, licensed under MIT.
-->
<template>
  <div
    class="pdm-login-characters"
    :data-state="characterState"
    :data-peeking="isPurplePeeking ? 'true' : 'false'"
    aria-hidden="true"
  >
    <div ref="purpleRef" class="pdm-login-character is-purple" :style="purpleStyle">
      <div class="pdm-character-eyes purple-eyes" :style="purpleEyesStyle">
        <span ref="purpleEyeLeft" class="white-eye purple-eye" :class="{ 'is-blinking': isPurpleBlinking }">
          <i :style="pupilStyle(purpleEyeLeft, 5, purpleForcedLook)" />
        </span>
        <span ref="purpleEyeRight" class="white-eye purple-eye" :class="{ 'is-blinking': isPurpleBlinking }">
          <i :style="pupilStyle(purpleEyeRight, 5, purpleForcedLook)" />
        </span>
      </div>
    </div>

    <div ref="blackRef" class="pdm-login-character is-charcoal" :style="blackStyle">
      <div class="pdm-character-eyes charcoal-eyes" :style="blackEyesStyle">
        <span ref="blackEyeLeft" class="white-eye charcoal-eye" :class="{ 'is-blinking': isBlackBlinking }">
          <i :style="pupilStyle(blackEyeLeft, 4, blackForcedLook)" />
        </span>
        <span ref="blackEyeRight" class="white-eye charcoal-eye" :class="{ 'is-blinking': isBlackBlinking }">
          <i :style="pupilStyle(blackEyeRight, 4, blackForcedLook)" />
        </span>
      </div>
    </div>

    <div ref="orangeRef" class="pdm-login-character is-orange" :style="orangeStyle">
      <div class="pdm-character-eyes orange-eyes" :style="orangeEyesStyle">
        <span ref="orangeEyeLeft" class="pupil-dot" :style="pupilStyle(orangeEyeLeft, 5, smallForcedLook)"></span>
        <span ref="orangeEyeRight" class="pupil-dot" :style="pupilStyle(orangeEyeRight, 5, smallForcedLook)"></span>
      </div>
    </div>

    <div ref="yellowRef" class="pdm-login-character is-yellow" :style="yellowStyle">
      <div class="pdm-character-eyes yellow-eyes" :style="yellowEyesStyle">
        <span ref="yellowEyeLeft" class="pupil-dot" :style="pupilStyle(yellowEyeLeft, 5, smallForcedLook)"></span>
        <span ref="yellowEyeRight" class="pupil-dot" :style="pupilStyle(yellowEyeRight, 5, smallForcedLook)"></span>
      </div>
      <span class="pdm-character-mouth" :style="yellowMouthStyle"></span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'

const props = withDefaults(defineProps<{
  isTyping?: boolean
  showPassword?: boolean
  passwordLength?: number
}>(), {
  isTyping: false,
  showPassword: false,
  passwordLength: 0,
})

type Point = { x: number; y: number }
type CharacterPosition = { faceX: number; faceY: number; bodySkew: number }
type Timer = ReturnType<typeof setTimeout>

const mouse = reactive({ x: 0, y: 0 })
const isPurpleBlinking = ref(false)
const isBlackBlinking = ref(false)
const isLookingAtEachOther = ref(false)
const isPurplePeeking = ref(false)

const purpleRef = ref<HTMLElement | null>(null)
const blackRef = ref<HTMLElement | null>(null)
const orangeRef = ref<HTMLElement | null>(null)
const yellowRef = ref<HTMLElement | null>(null)
const purpleEyeLeft = ref<HTMLElement | null>(null)
const purpleEyeRight = ref<HTMLElement | null>(null)
const blackEyeLeft = ref<HTMLElement | null>(null)
const blackEyeRight = ref<HTMLElement | null>(null)
const orangeEyeLeft = ref<HTMLElement | null>(null)
const orangeEyeRight = ref<HTMLElement | null>(null)
const yellowEyeLeft = ref<HTMLElement | null>(null)
const yellowEyeRight = ref<HTMLElement | null>(null)

let purpleBlinkTimer: Timer | undefined
let purpleBlinkResetTimer: Timer | undefined
let blackBlinkTimer: Timer | undefined
let blackBlinkResetTimer: Timer | undefined
let lookingTimer: Timer | undefined
let peekTimer: Timer | undefined
let peekResetTimer: Timer | undefined

const isShowingPassword = computed(() => props.passwordLength > 0 && props.showPassword)
const isHidingPassword = computed(() => props.passwordLength > 0 && !props.showPassword)

const characterState = computed(() => {
  if (isShowingPassword.value) return 'showing-password'
  if (isHidingPassword.value) return 'hiding-password'
  if (props.isTyping) return 'typing'
  return 'idle'
})

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, value))
}

function calculatePosition(element: HTMLElement | null): CharacterPosition {
  if (!element) return { faceX: 0, faceY: 0, bodySkew: 0 }
  const rect = element.getBoundingClientRect()
  const deltaX = mouse.x - (rect.left + rect.width / 2)
  const deltaY = mouse.y - (rect.top + rect.height / 3)
  return {
    faceX: clamp(deltaX / 20, -15, 15),
    faceY: clamp(deltaY / 30, -10, 10),
    bodySkew: clamp(-deltaX / 120, -6, 6),
  }
}

function calculatePupilPosition(element: HTMLElement | null, maxDistance: number, forced?: Point | null): Point {
  if (forced) return forced
  if (!element) return { x: 0, y: 0 }
  const rect = element.getBoundingClientRect()
  const deltaX = mouse.x - (rect.left + rect.width / 2)
  const deltaY = mouse.y - (rect.top + rect.height / 2)
  const distance = Math.min(Math.hypot(deltaX, deltaY), maxDistance)
  const angle = Math.atan2(deltaY, deltaX)
  return {
    x: Math.cos(angle) * distance,
    y: Math.sin(angle) * distance,
  }
}

function pupilStyle(element: HTMLElement | null, maxDistance: number, forced?: Point | null) {
  const position = calculatePupilPosition(element, maxDistance, forced)
  return { transform: `translate(${position.x}px, ${position.y}px)` }
}

const purplePosition = computed(() => calculatePosition(purpleRef.value))
const blackPosition = computed(() => calculatePosition(blackRef.value))
const orangePosition = computed(() => calculatePosition(orangeRef.value))
const yellowPosition = computed(() => calculatePosition(yellowRef.value))

const purpleStyle = computed(() => {
  let transform = `skewX(${purplePosition.value.bodySkew}deg)`
  if (isShowingPassword.value) {
    transform = 'skewX(0deg)'
  } else if (props.isTyping || isHidingPassword.value) {
    transform = `skewX(${purplePosition.value.bodySkew - 12}deg) translateX(40px)`
  }
  return {
    height: props.isTyping || isHidingPassword.value ? '440px' : '400px',
    transform,
  }
})

const blackStyle = computed(() => {
  let transform = `skewX(${blackPosition.value.bodySkew}deg)`
  if (isShowingPassword.value) {
    transform = 'skewX(0deg)'
  } else if (isLookingAtEachOther.value) {
    transform = `skewX(${blackPosition.value.bodySkew * 1.5 + 10}deg) translateX(20px)`
  } else if (props.isTyping || isHidingPassword.value) {
    transform = `skewX(${blackPosition.value.bodySkew * 1.5}deg)`
  }
  return { transform }
})

const orangeStyle = computed(() => ({
  transform: isShowingPassword.value ? 'skewX(0deg)' : `skewX(${orangePosition.value.bodySkew}deg)`,
}))

const yellowStyle = computed(() => ({
  transform: isShowingPassword.value ? 'skewX(0deg)' : `skewX(${yellowPosition.value.bodySkew}deg)`,
}))

const purpleEyesStyle = computed(() => ({
  left: `${isShowingPassword.value ? 20 : isLookingAtEachOther.value ? 55 : 45 + purplePosition.value.faceX}px`,
  top: `${isShowingPassword.value ? 35 : isLookingAtEachOther.value ? 65 : 40 + purplePosition.value.faceY}px`,
}))

const blackEyesStyle = computed(() => ({
  left: `${isShowingPassword.value ? 10 : isLookingAtEachOther.value ? 32 : 26 + blackPosition.value.faceX}px`,
  top: `${isShowingPassword.value ? 28 : isLookingAtEachOther.value ? 12 : 32 + blackPosition.value.faceY}px`,
}))

const orangeEyesStyle = computed(() => ({
  left: `${isShowingPassword.value ? 50 : 82 + orangePosition.value.faceX}px`,
  top: `${isShowingPassword.value ? 85 : 90 + orangePosition.value.faceY}px`,
}))

const yellowEyesStyle = computed(() => ({
  left: `${isShowingPassword.value ? 20 : 52 + yellowPosition.value.faceX}px`,
  top: `${isShowingPassword.value ? 35 : 40 + yellowPosition.value.faceY}px`,
}))

const yellowMouthStyle = computed(() => ({
  left: `${isShowingPassword.value ? 10 : 40 + yellowPosition.value.faceX}px`,
  top: `${isShowingPassword.value ? 88 : 88 + yellowPosition.value.faceY}px`,
}))

const purpleForcedLook = computed<Point | null>(() => {
  if (isShowingPassword.value) return isPurplePeeking.value ? { x: 4, y: 5 } : { x: -4, y: -4 }
  if (isLookingAtEachOther.value) return { x: 3, y: 4 }
  return null
})

const blackForcedLook = computed<Point | null>(() => {
  if (isShowingPassword.value) return { x: -4, y: -4 }
  if (isLookingAtEachOther.value) return { x: 0, y: -4 }
  return null
})

const smallForcedLook = computed<Point | null>(() => (
  isShowingPassword.value ? { x: -5, y: -4 } : null
))

function trackPointer(event: PointerEvent) {
  mouse.x = event.clientX
  mouse.y = event.clientY
}

function schedulePurpleBlink() {
  purpleBlinkTimer = setTimeout(() => {
    isPurpleBlinking.value = true
    purpleBlinkResetTimer = setTimeout(() => {
      isPurpleBlinking.value = false
      schedulePurpleBlink()
    }, 150)
  }, Math.random() * 4000 + 3000)
}

function scheduleBlackBlink() {
  blackBlinkTimer = setTimeout(() => {
    isBlackBlinking.value = true
    blackBlinkResetTimer = setTimeout(() => {
      isBlackBlinking.value = false
      scheduleBlackBlink()
    }, 150)
  }, Math.random() * 4000 + 3000)
}

function clearPeekTimers() {
  clearTimeout(peekTimer)
  clearTimeout(peekResetTimer)
  peekTimer = undefined
  peekResetTimer = undefined
}

function schedulePeek() {
  clearPeekTimers()
  isPurplePeeking.value = false
  if (!isShowingPassword.value) return
  peekTimer = setTimeout(() => {
    isPurplePeeking.value = true
    peekResetTimer = setTimeout(() => {
      isPurplePeeking.value = false
      schedulePeek()
    }, 800)
  }, Math.random() * 3000 + 2000)
}

watch(() => props.isTyping, (typing) => {
  clearTimeout(lookingTimer)
  isLookingAtEachOther.value = typing
  if (typing) {
    lookingTimer = setTimeout(() => {
      isLookingAtEachOther.value = false
    }, 800)
  }
})

watch([() => props.passwordLength, () => props.showPassword], schedulePeek)

onMounted(() => {
  window.addEventListener('pointermove', trackPointer)
  schedulePurpleBlink()
  scheduleBlackBlink()
})

onBeforeUnmount(() => {
  window.removeEventListener('pointermove', trackPointer)
  clearTimeout(purpleBlinkTimer)
  clearTimeout(purpleBlinkResetTimer)
  clearTimeout(blackBlinkTimer)
  clearTimeout(blackBlinkResetTimer)
  clearTimeout(lookingTimer)
  clearPeekTimers()
})
</script>

<style scoped>
.pdm-login-characters {
  position: relative;
  width: 550px;
  height: 400px;
}

.pdm-login-character {
  position: absolute;
  bottom: 0;
  transform-origin: bottom center;
  transition: all 700ms ease-in-out;
}

.is-purple { left: 70px; z-index: 1; width: 180px; height: 400px; border-radius: 10px 10px 0 0; background: #6c3ff5; }
.is-charcoal { left: 240px; z-index: 2; width: 120px; height: 310px; border-radius: 8px 8px 0 0; background: #2d2d2d; }
.is-orange { left: 0; z-index: 3; width: 240px; height: 200px; border-radius: 120px 120px 0 0; background: #ff9b6b; }
.is-yellow { left: 310px; z-index: 4; width: 140px; height: 230px; border-radius: 70px 70px 0 0; background: #e8d754; }

.pdm-character-eyes {
  position: absolute;
  display: flex;
}

.purple-eyes { gap: 32px; transition: all 700ms ease-in-out; }
.charcoal-eyes { gap: 24px; transition: all 700ms ease-in-out; }
.orange-eyes { gap: 32px; }
.yellow-eyes { gap: 24px; }
.orange-eyes, .yellow-eyes, .pdm-character-mouth { transition: all 200ms ease-out; }

.white-eye {
  display: grid;
  overflow: hidden;
  place-items: center;
  border-radius: 50%;
  background: #fff;
  transition: height 150ms ease;
}

.purple-eye { width: 18px; height: 18px; }
.charcoal-eye { width: 16px; height: 16px; }
.white-eye.is-blinking { height: 2px; }

.white-eye i {
  display: block;
  border-radius: 50%;
  background: #2d2d2d;
  transition: transform 100ms ease-out;
}

.white-eye.is-blinking i { visibility: hidden; }
.purple-eye i { width: 7px; height: 7px; }
.charcoal-eye i { width: 6px; height: 6px; }

.pupil-dot {
  display: block;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: #2d2d2d;
  transition: transform 100ms ease-out;
}

.pdm-character-mouth {
  position: absolute;
  width: 80px;
  height: 4px;
  border-radius: 999px;
  background: #2d2d2d;
}

@media (prefers-reduced-motion: reduce) {
  .pdm-login-character,
  .pdm-character-eyes,
  .white-eye,
  .white-eye i,
  .pupil-dot,
  .pdm-character-mouth {
    transition: none;
  }
}
</style>
