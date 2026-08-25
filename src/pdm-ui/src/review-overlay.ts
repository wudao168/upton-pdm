import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/app.css'
import './styles/review-overlay.css'
import DrawingReviewOverlayApp from './components/DrawingReviewOverlayApp.vue'

createApp(DrawingReviewOverlayApp).use(ElementPlus, { locale: zhCn }).mount('#review-overlay-root')
