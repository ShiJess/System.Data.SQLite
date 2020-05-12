
参考链接
* https://www.fossil-scm.org/ 
https://fossil-scm.org/home/doc/trunk/www/mirrortogithub.md

首次迁移流程

* cd ./.fossil-repo
* Clone源代码
* ../fossil.exe clone https://system.data.sqlite.org/ sds.fossil
* 打开源代码库连接
* ../fossil.exe open sds.fossil

//设置git镜像
../fossil.exe git export /.git-mirror --autopush  https://ShiJess:******@github.com/ShiJess/System.Data.SQLite.git


//指定镜像处理文件夹
../fossil.exe git export ./.git-mirror
cd ./.git_mirror
git push --mirror https://ShiJess:******@github.com/ShiJess/System.Data.SQLite.git


后续镜像更新

//获取最新源代码
../fossil.exe update

//镜像代码更新
../fossil.exe git export

//查看镜像代码状态
../fossil.exe git status


//关闭源代码库连接
../fossil.exe close

换电脑后，需要执行
../fossil.exe git export ./.git-mirror


sds.fossil —— 版本库
.git-mirror —— 镜像


## 开源说明

* [官方说明](http://system.data.sqlite.org/index.html/doc/trunk/www/copyright.wiki)
    * "System.Data.SQLite.Linq/SQL Generation" 目录下的代码开源协议 Microsoft Public License (MS-PL).
    * 其他文件使用 UnLicense 【Public Domain】

