# File-manager interface

## Direction contract

THESIS: Keep the user in two file locations with explicit source/destination focus and immediately available operations.

OWN-WORLD: Compact desktop controls, restrained dark and light neutral surfaces, one blue source header, flat lists, thin dividers, readable system UI typography, vector navigation icons and text operation labels.

STORY: Navigate or search, select real files, inspect a preview, then perform an operation with a visible destination and result. Keyboard actions and toolbar actions use the same command router.

FIRST VIEWPORT: Menu and operations at top, search below them, optional tree at left, independent file panes in the center, optional preview at right, per-pane tabs below the lists, operation progress and status at bottom. Default 1400 by 860; usable down to 1000 by 650.

FORM: Compact, source/destination dual-pane layout. Visual similarity is a design goal, not evidence of proprietary-product parity.

FINISH: Headless rendering at wide and compact sizes, actual keyboard and operation interactions, and separately recorded Mac acceptance limits.

## Tokens

Text: 13 px system UI. File rows: 25 px. Controls: minimum 28 px. Window minimum: 1000 by 650. Toolbar gaps: 5 px. Pane source: #2C5791 with white text. Dark surface/inset/divider/muted: #25272C / #1E2024 / #44474E / #B5BAC4. Light equivalents: #F0F1F3 / #FFFFFF / #C8CBD2 / #505A69. Controls use Fluent compact styling; pane and list geometry follows the reference hierarchy.
