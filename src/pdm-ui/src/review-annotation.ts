import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/app.css'
import './styles/review-annotation.css'
import DrawingReviewAnnotationOverlayApp from './components/DrawingReviewAnnotationOverlayApp.vue'

createApp(DrawingReviewAnnotationOverlayApp).use(ElementPlus, { locale: zhCn }).mount('#review-annotation-root')
